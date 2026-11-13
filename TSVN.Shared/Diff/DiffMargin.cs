using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Outlining;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// The vertical colour blocks drawn between the line numbers and the text.
    ///
    /// One marker per changed run: amber for lines that do not exist in BASE, a washed out
    /// amber for edited lines and grey where BASE lines were removed.
    ///
    /// Geometry is never derived from line numbers or from the scroll offset. Every rectangle is
    /// built from the coordinates Visual Studio assigned to the lines it actually rendered, so
    /// scrolling, word wrap and variable line heights are all handled by the same numbers the
    /// text itself is drawn with.
    ///
    /// A changed run that is entirely hidden inside a collapsed outlining region is drawn on that
    /// region's header line instead: the header is the line the editor shows in place of the folded
    /// text, and it is a real rendered line, so the marker can never land in empty space below the
    /// code.
    /// </summary>
    public sealed class DiffMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffMargin";

        private const double GutterWidth = 6.0;
        private const double MarkerLeft = 1.0;
        private const double MarkerWidth = GutterWidth - 2.0;

        /// <summary>
        /// How often a render may be retried while the view has no usable layout. A chunk list
        /// can arrive before the first layout pass, and without a retry the strip stays empty
        /// until the user scrolls.
        /// </summary>
        private const int MaxRetryCount = 8;

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

        private readonly IWpfTextView _textView;
        private readonly MarkerStrip _strip;
        private readonly Canvas _canvas;
        private readonly DiffMarkersController _controller;
        private readonly DispatcherTimer _retry;
        private readonly string _filePath;
        private readonly OutlineSource _outlines;

        private DiffPalette _palette;
        private bool _disposed;
        private int _retryCount;
        private bool _settled;

        private List<DiffFolds.Range> _folds;
        private ITextSnapshot _foldsSnapshot;

        public DiffMargin(IWpfTextView textView, IOutliningManagerService outliningService)
        {
            _textView = textView;
            _outlines = new OutlineSource(textView, outliningService);

            _strip = new MarkerStrip(
                GutterWidth,
                MarkerLeft,
                MarkerWidth,
                1.5,
                true,
                VerticalAlignment.Top);

            _canvas = _strip.Element;

            _retry = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = RetryDelay
            };

            _retry.Tick += OnRetryTick;

            _textView.LayoutChanged += OnLayoutChanged;
            _textView.ZoomLevelChanged += OnZoomLevelChanged;
            _textView.ViewportHeightChanged += OnViewportHeightChanged;
            _textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
            _textView.Closed += OnTextViewClosed;

            _outlines.Changed += OnRegionsChanged;

            var filePath = GetFilePath(textView.TextBuffer);

            if (string.IsNullOrEmpty(filePath))
            {
                // A projection buffer (diff view, peek) carries the document on the underlying
                // buffer instead of on the one it exposes.
                filePath = GetFilePath(textView.TextDataModel.DocumentBuffer);
            }

            _filePath = filePath;

            if (!string.IsNullOrEmpty(filePath))
            {
                _controller = new DiffMarkersController(textView, filePath);
                _controller.ChunksChanged += OnChunksChanged;
                _controller.Start();
            }
        }

        public event EventHandler Disposed;

        public FrameworkElement VisualElement
        {
            get { return _canvas; }
        }

        public double MarginSize
        {
            get { return GutterWidth; }
        }

        public bool Enabled
        {
            get { return true; }
        }

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _textView.LayoutChanged -= OnLayoutChanged;
            _textView.ZoomLevelChanged -= OnZoomLevelChanged;
            _textView.ViewportHeightChanged -= OnViewportHeightChanged;
            _textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
            _textView.Closed -= OnTextViewClosed;

            _outlines.Changed -= OnRegionsChanged;
            _outlines.Dispose();

            _retry.Stop();
            _retry.Tick -= OnRetryTick;

            if (_controller != null)
            {
                _controller.ChunksChanged -= OnChunksChanged;
                _controller.Dispose();
            }

            _strip.Clear();

            var handler = Disposed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        public static string GetFilePath(ITextBuffer buffer)
        {
            if (buffer == null)
            {
                return null;
            }

            ITextDocument document;

            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out document) && document != null)
            {
                return document.FilePath;
            }

            return null;
        }

        private void OnTextViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void OnBackgroundBrushChanged(object sender, BackgroundBrushChangedEventArgs e)
        {
            _palette = null;
            Render();
        }

        private void OnViewportHeightChanged(object sender, EventArgs e)
        {
            // A zoom change arrives here as well as through the layout: re-render so the strip is
            // rescaled even when the line coordinates themselves come out unchanged.
            Render();
        }

        private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            // The one signal that says the scale itself changed, including when Visual Studio
            // restores a non default zoom for the view that is being opened.
            Render();
        }

        private void OnChunksChanged(object sender, EventArgs e)
        {
            Render();
        }

        private void OnRegionsChanged(object sender, EventArgs e)
        {
            // A fold changes which lines are on screen but not the buffer, so the cached ranges have
            // to go before the next layout is mapped.
            _folds = null;
            Render();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // Rendered in place, not deferred: the marker positions have to be recomputed from the
            // layout this event announces, and any queueing here shows up as markers that trail
            // the text while scrolling.
            Render();
        }

        private void OnRetryTick(object sender, EventArgs e)
        {
            _retry.Stop();
            Render();
        }

        private void Render()
        {
            if (_disposed)
            {
                return;
            }

            var lines = _textView.TextViewLines;

            // A layout pass can still hand out the previous, already invalidated line collection.
            // Nothing is removed from the canvas in that case: the markers already on screen stay
            // until a layout with usable coordinates comes along.
            if (lines == null || !lines.IsValid)
            {
                RestartRetry();
                return;
            }

            _retry.Stop();
            _retryCount = 0;

            var viewportHeight = _textView.ViewportHeight;

            var markers = new List<MarkerRect>();

            // A zero height view renders no text at all, so there is nothing to align to. Auxiliary
            // views (peek, preview) report this until they get a layout of their own.
            if (viewportHeight > 0.0 && DiffMarkersState.IsEnabled(_filePath))
            {
                if (_palette == null)
                {
                    _palette = DiffColorProvider.For(_textView.Background);
                }

                var chunks = _controller == null ? null : _controller.Chunks;

                // Only the rendered lines. A line hidden by a collapsed outlining region has no
                // entry here, which is what tells the marker builder that a run is folded away.
                var visible = BuildVisibleLines(lines);

                // Measured on every render, never remembered: the editor can already be zoomed
                // when the margin is first composed, so a value read once at the default would
                // stay wrong until something forced a recalculation.
                var scale = ViewScale.Resolve(_textView, _canvas);
                var canvasHeight = ViewScale.ToCanvas(viewportHeight, scale);
                var shift = -ResolveOrigin(lines, _textView.ViewportTop);

                if (chunks != null && chunks.Count > 0 && visible.Count > 0)
                {
                    markers = BuildMarkers(
                        chunks,
                        visible,
                        _palette,
                        canvasHeight,
                        shift,
                        scale,
                        GetFolds());
                }

                // A Canvas has no intrinsic size. Without an explicit height the margin container
                // arranges it at 0x0 and ClipToBounds throws every marker away, which looks exactly
                // like the margin was never created.
                _strip.SetHeight(canvasHeight);

                // The first usable layout is not necessarily the final one: a view created while
                // the editor is still zooming in reports metrics from before the zoom, and no
                // further layout event follows to correct them. One deferred render, on the timer
                // that already exists, costs nothing and settles the strip.
                if (!_settled)
                {
                    _settled = true;
                    RestartRetry();
                }
            }

            _strip.Apply(markers);
        }

        /// <summary>Collapsed regions for the current snapshot, recollected only when the buffer changes.</summary>
        private List<DiffFolds.Range> GetFolds()
        {
            var snapshot = _textView.TextSnapshot;

            if (_folds != null && ReferenceEquals(_foldsSnapshot, snapshot))
            {
                return _folds;
            }

            _foldsSnapshot = snapshot;
            _folds = DiffFolds.Collect(_outlines.Manager, snapshot);

            return _folds;
        }

        /// <summary>
        /// The coordinate the line extents are measured from: the value that has to be subtracted
        /// from <see cref="ITextViewLine.Top"/> to get a distance from the top of the viewport.
        ///
        /// Decided by a fact the editor guarantees rather than by a guess: the first visible line
        /// is, by definition, the line that contains the top edge of the viewport. Whichever
        /// candidate origin falls inside that line's extent is the space the extents are in.
        ///
        /// The previous version counted how many lines each origin put on screen and kept 0 on a
        /// tie. A short document - which is what collapsing a large region produces - fits on
        /// screen from either origin, so the tie went to 0 and every marker dropped by exactly
        /// ViewportTop, landing in the empty space below the last line.
        /// </summary>
        private static double ResolveOrigin(ITextViewLineCollection lines, double viewportTop)
        {
            var first = lines.FirstVisibleLine;

            if (first == null)
            {
                return viewportTop;
            }

            if (viewportTop >= first.Top && viewportTop < first.Bottom)
            {
                return viewportTop;
            }

            if (0.0 >= first.Top && 0.0 < first.Bottom)
            {
                return 0.0;
            }

            // Neither: an inconsistent collection mid layout. The documented space is the safe one.
            return viewportTop;
        }

        private void RestartRetry()
        {
            if (_disposed || _retry.IsEnabled || _retryCount >= MaxRetryCount)
            {
                return;
            }

            _retryCount++;
            _retry.Start();
        }

        /// <summary>
        /// Builds one rectangle per unbroken stretch of rendered lines that belongs to a changed
        /// run. Because the stretches are taken from the rendered line list, a run that spans more
        /// than the viewport is painted as the part of it that is on screen, and a run that is
        /// scrolled away simply produces nothing.
        ///
        /// A run with no rendered line at all is either folded away or off screen. Folding is
        /// recognised from the outlining state, not from the gap between line numbers: a run whose
        /// header is known is painted on that header, and anything else is dropped. That is what
        /// keeps a marker from appearing in the empty area below the last line of a folded file.
        /// </summary>
        private static List<MarkerRect> BuildMarkers(
            IReadOnlyList<DiffChunk> chunks,
            IReadOnlyList<LineSpan> visible,
            DiffPalette palette,
            double canvasHeight,
            double shift,
            double scale,
            List<DiffFolds.Range> folds)
        {
            var markers = new List<MarkerRect>();
            var folded = new List<FoldMark>();

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var startLine = chunk.StartLine - 1;
                var endLine = chunk.EndLine - 1;

                var first = FirstAtOrAfter(visible, startLine);

                if (first == visible.Count || visible[first].LineNumber > endLine)
                {
                    var header = DiffFolds.HeaderFor(folds, startLine);

                    if (header < 0 && first > 0 && first < visible.Count
                        && visible[first - 1].LineNumber < startLine)
                    {
                        // No outlining information, but rendered lines bracket the run, so it can
                        // only be hidden by a fold. The line before it is then its header.
                        header = visible[first - 1].LineNumber;
                    }

                    if (header >= 0)
                    {
                        AddFoldMark(folded, header, chunk);
                    }

                    continue;
                }

                var index = first;

                while (index < visible.Count)
                {
                    // Line coordinates are moved onto the canvas by the origin resolved for this
                    // layout and then scaled: the text view and the margin are separate visuals,
                    // and the editor's zoom changes the gap between them.
                    var top = Math.Max(0.0, ViewScale.ToCanvas(visible[index].Top + shift, scale));
                    var bottom = ViewScale.ToCanvas(visible[index].Bottom + shift, scale);
                    var lineNumber = visible[index].LineNumber;
                    var next = index + 1;

                    // Extend over the following lines of the same run. A jump in the line numbers
                    // is a fold, so it starts a new stretch instead of one block spanning the fold.
                    while (next < visible.Count
                           && visible[next].LineNumber == lineNumber + 1
                           && visible[next].LineNumber <= endLine)
                    {
                        bottom = ViewScale.ToCanvas(visible[next].Bottom + shift, scale);
                        lineNumber = visible[next].LineNumber;
                        next++;
                    }

                    // A run that continues past the last rendered line fills the view down to its
                    // bottom edge; one that ends on screen stops at its last line.
                    var runsOffScreen = next == visible.Count && lineNumber < endLine;

                    bottom = runsOffScreen
                        ? Math.Max(bottom, canvasHeight)
                        : Math.Min(bottom, canvasHeight);

                    if (bottom > 0.0 && top < canvasHeight)
                    {
                        markers.Add(new MarkerRect(
                            top,
                            Math.Max(1.0, bottom - top - 1.0),
                            palette.ForKind(chunk.Kind),
                            Describe(chunk)));
                    }

                    if (next >= visible.Count || visible[next].LineNumber > endLine)
                    {
                        break;
                    }

                    index = next;
                }
            }

            AppendFoldMarkers(markers, folded, visible, palette, canvasHeight, shift, scale);

            return markers;
        }

        /// <summary>Records that a fold hides a changed run; repeated runs on one fold are merged.</summary>
        private static void AddFoldMark(List<FoldMark> folded, int header, DiffChunk chunk)
        {
            for (var i = 0; i < folded.Count; i++)
            {
                if (folded[i].Header != header)
                {
                    continue;
                }

                var existing = folded[i];

                existing.Kind = Stronger(existing.Kind, chunk.Kind);
                existing.Lines += Math.Max(1, chunk.LineCount);
                folded[i] = existing;

                return;
            }

            folded.Add(new FoldMark
            {
                Header = header,
                Kind = chunk.Kind,
                Lines = Math.Max(1, chunk.LineCount)
            });
        }

        /// <summary>Solid amber wins over grey, so a fold that gained and lost lines reads as changed.</summary>
        private static DiffKind Stronger(DiffKind left, DiffKind right)
        {
            return Rank(left) >= Rank(right) ? left : right;
        }

        private static int Rank(DiffKind kind)
        {
            switch (kind)
            {
                case DiffKind.Added: return 2;
                case DiffKind.Modified: return 1;
                default: return 0;
            }
        }

        /// <summary>
        /// Paints the folds that hide changed runs. The marker covers the header line, which is the
        /// only line the editor still shows for that stretch of text.
        /// </summary>
        private static void AppendFoldMarkers(
            List<MarkerRect> markers,
            List<FoldMark> folded,
            IReadOnlyList<LineSpan> visible,
            DiffPalette palette,
            double canvasHeight,
            double shift,
            double scale)
        {
            for (var i = 0; i < folded.Count; i++)
            {
                var mark = folded[i];
                var index = IndexOfLine(visible, mark.Header);

                if (index < 0)
                {
                    // The header is scrolled out of view; the fold itself is not on screen either.
                    continue;
                }

                var top = Math.Max(0.0, ViewScale.ToCanvas(visible[index].Top + shift, scale));
                var bottom = Math.Min(canvasHeight, ViewScale.ToCanvas(visible[index].Bottom + shift, scale));

                if (bottom <= 0.0 || top >= canvasHeight)
                {
                    continue;
                }

                markers.Add(new MarkerRect(
                    top,
                    Math.Max(1.0, bottom - top - 1.0),
                    palette.ForKind(mark.Kind),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} changed line(s) hidden in a collapsed region",
                        mark.Lines)));
            }
        }

        /// <summary>
        /// Vertical extent of every buffer line the view is currently rendering, ordered by line
        /// number. A wrapped line produces several text view lines, so the extents are merged.
        /// </summary>
        private static List<LineSpan> BuildVisibleLines(ITextViewLineCollection lines)
        {
            var visible = new List<LineSpan>();

            foreach (var line in lines)
            {
                // A collection handed out while a fold is collapsing still holds the lines that are
                // on their way out, with the extents they had before the fold. Their coordinates no
                // longer correspond to anything on screen, so they are the one thing that can put a
                // marker in the empty space the fold left behind.
                if (line.VisibilityState == VisibilityState.Hidden)
                {
                    continue;
                }

                var number = line.Start.GetContainingLine().LineNumber;

                if (visible.Count > 0 && visible[visible.Count - 1].LineNumber == number)
                {
                    var previous = visible[visible.Count - 1];
                    visible[visible.Count - 1] = new LineSpan(
                        number,
                        Math.Min(previous.Top, line.Top),
                        Math.Max(previous.Bottom, line.Bottom));

                    continue;
                }

                visible.Add(new LineSpan(number, line.Top, line.Bottom));
            }

            return visible;
        }

        /// <summary>Index of the rendered line carrying <paramref name="lineNumber"/>, or -1.</summary>
        private static int IndexOfLine(IReadOnlyList<LineSpan> visible, int lineNumber)
        {
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i].LineNumber == lineNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Index of the first rendered line at or after <paramref name="lineNumber"/>, or the count.</summary>
        private static int FirstAtOrAfter(IReadOnlyList<LineSpan> visible, int lineNumber)
        {
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i].LineNumber >= lineNumber)
                {
                    return i;
                }
            }

            return visible.Count;
        }

        private static string Describe(DiffChunk chunk)
        {
            switch (chunk.Kind)
            {
                case DiffKind.Added:
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} line(s) added since BASE",
                        chunk.LineCount);

                case DiffKind.Deleted:
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} line(s) removed since BASE",
                        chunk.OldLineCount);

                default:
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} line(s) changed since BASE",
                        chunk.LineCount);
            }
        }

        /// <summary>A fold that hides at least one changed line.</summary>
        private struct FoldMark
        {
            public int Header;

            public DiffKind Kind;

            public int Lines;
        }

        private struct LineSpan
        {
            public LineSpan(int lineNumber, double top, double bottom)
            {
                LineNumber = lineNumber;
                Top = top;
                Bottom = bottom;
            }

            public int LineNumber { get; private set; }

            public double Top { get; private set; }

            public double Bottom { get; private set; }
        }
    }
}
