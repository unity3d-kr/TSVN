using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// A four pixel map of the whole file next to the vertical scroll bar, so changed regions
    /// stay visible when they are scrolled out of view.
    ///
    /// It is a passive view over <see cref="DiffChunkCache"/>: the gutter margin owns the
    /// controller, this one only paints what it finds there.
    ///
    /// Positions are computed in displayed lines, not buffer lines. Collapsed outlining regions
    /// remove height from the document, which is why the scroll bar's thumb changes size while
    /// editing; mapping raw line numbers puts every mark further off the moment anything is
    /// folded.
    /// </summary>
    public sealed class DiffOverviewMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffOverviewMargin";

        private const double StripWidth = 4.0;
        private const double MinimumMarkHeight = 2.0;

        /// <summary>Last reported mapping; see <see cref="Report"/>. Temporary.</summary>
        private static string _reported;

        private readonly IWpfTextView _textView;
        private readonly ITextBuffer _buffer;
        private readonly MarkerStrip _strip;
        private readonly OutlineSource _outlines;

        private DiffPalette _palette;
        private bool _disposed;

        private List<DiffFolds.Range> _folds;
        private ITextSnapshot _foldsSnapshot;

        public DiffOverviewMargin(IWpfTextView textView, IOutliningManagerService outliningService)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;
            _outlines = new OutlineSource(textView, outliningService);

            // Stretched rather than measured: the strip follows the height of the margin container,
            // which is the same height the scroll bar is given.
            _strip = new MarkerStrip(
                StripWidth,
                0.0,
                StripWidth,
                0.0,
                false,
                VerticalAlignment.Stretch);

            _textView.LayoutChanged += OnLayoutChanged;
            _textView.ZoomLevelChanged += OnZoomLevelChanged;
            _textView.ViewportHeightChanged += OnViewportHeightChanged;
            _textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
            _textView.Closed += OnTextViewClosed;

            DiffChunkCache.Changed += OnChunksChanged;
            DiffMarkersState.Changed += OnStateChanged;

            _outlines.Changed += OnRegionsChanged;
        }

        public event EventHandler Disposed;

        public FrameworkElement VisualElement
        {
            get { return _strip.Element; }
        }

        public double MarginSize
        {
            get { return StripWidth; }
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

            DiffChunkCache.Changed -= OnChunksChanged;
            DiffMarkersState.Changed -= OnStateChanged;
            _outlines.Changed -= OnRegionsChanged;
            _outlines.Dispose();

            _strip.Clear();

            var handler = Disposed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
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
            // The strip is stretched to the container, so its height can change without any line
            // coordinate changing. The mapping depends on that height.
            Render();
        }

        private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            // Everything the marks are laid out against - the track, the line height, the canvas
            // scale - is tied to the zoom, and it can already be non default when the view opens.
            Render();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            Render();
        }

        private void OnChunksChanged(object sender, ITextBuffer buffer)
        {
            if (!ReferenceEquals(buffer, _buffer))
            {
                return;
            }

            Render();
        }

        private void OnRegionsChanged(object sender, EventArgs e)
        {
            // A fold changes the displayed height of the document, so every mark moves.
            _folds = null;
            Render();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // Not only on resize: folding and unfolding change the mapping without the viewport
            // changing height, and the marks have to follow.
            Render();
        }

        private void Render()
        {
            if (_disposed)
            {
                return;
            }

            var marks = new List<MarkerRect>();

            var chunks = DiffMarkersState.ShowOverviewBar && DiffMarkersState.IsEnabled(DiffMargin.GetFilePath(_buffer))
                ? DiffChunkCache.Get(_buffer)
                : null;

            var snapshot = _textView.TextSnapshot;
            var height = _textView.ViewportHeight;

            if (chunks != null && chunks.Count > 0 && height > 0.0 && snapshot.LineCount > 0)
            {
                if (_palette == null)
                {
                    _palette = DiffColorProvider.For(_textView.Background);
                }

                var palette = _palette;
                var folds = GetFolds(snapshot);

                // Measured on every render: the strip is a sibling of the text, so the editor's
                // zoom stretches one and not the other, and the scroll bar the marks have to line
                // up with is drawn at the strip's scale.
                var scale = ViewScale.Resolve(_textView, _strip.Element);

                // The strip's arranged height, which is the scroll bar's height as well: both are
                // children of the same margin container. Derived from the viewport only as a
                // fallback, because ViewportHeight is in text units and has to be scaled first.
                var canvasHeight = _strip.Element.ActualHeight > 0.0
                    ? _strip.Element.ActualHeight
                    : ViewScale.ToCanvas(height, scale);

                // The scroll bar measures the document in displayed lines: a collapsed region costs
                // nothing but its header.
                var displayLines = Math.Max(1, snapshot.LineCount - DiffFolds.HiddenTotal(folds));
                var visibleLines = Math.Max(1.0, Math.Round(canvasHeight / ViewScale.ToCanvas(GetLineHeight(), scale)));

                // The document is mapped onto the track minus the thumb, which is exactly the space
                // the scroll bar has to move the thumb through.
                var track = displayLines > visibleLines
                    ? Math.Max(1.0, canvasHeight * (1.0 - visibleLines / displayLines))
                    : canvasHeight;

                // The thumb travels over that track only: the first displayed line sits at the top
                // when the view is scrolled to the start, and the last reachable first line -
                // displayLines minus the ones the viewport shows - sits at the bottom of it.
                // Dividing by displayLines puts the end of the file short of the end of the track,
                // which on a folded document is a hundred pixels of drift by the bottom of the file.
                var span = Math.Max(1.0, displayLines - visibleLines);

                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    var startLine = chunk.StartLine - 1;
                    var endLine = chunk.EndLine - 1;

                    var displayStart = startLine - DiffFolds.HiddenBefore(folds, startLine);
                    var displayEnd = endLine - DiffFolds.HiddenBefore(folds, endLine);

                    var ratio = displayStart / span;
                    var top = ratio * track;

                    // Same span as the position: a chunk of L displayed rows covers L rows of the
                    // document, which is L / (displayLines - visibleLines) of the track.
                    var markHeight = Math.Max(
                        MinimumMarkHeight,
                        (displayEnd - displayStart + 1) / span * track);

                    if (top + markHeight > canvasHeight)
                    {
                        top = Math.Max(0, canvasHeight - markHeight);
                    }

                    marks.Add(new MarkerRect(
                        top,
                        markHeight,
                        palette.ForKind(chunk.Kind),
                        null));
                }

                Report(_outlines.Manager != null, folds.Count, DiffFolds.HiddenTotal(folds), displayLines, (int)visibleLines, track, canvasHeight, marks, _strip.Element.ActualHeight);
            }

            _strip.Apply(marks);
        }

        /// <summary>Collapsed regions of the current snapshot, recollected only when it changes.</summary>
        private List<DiffFolds.Range> GetFolds(ITextSnapshot snapshot)
        {
            if (_folds != null && ReferenceEquals(_foldsSnapshot, snapshot))
            {
                return _folds;
            }

            _foldsSnapshot = snapshot;
            _folds = DiffFolds.Collect(_outlines.Manager, snapshot);

            return _folds;
        }

        /// <summary>
        /// Writes one line whenever the mapping inputs change, to %LOCALAPPDATA%\TSVN\overview.log.
        ///
        /// Temporary: it separates "the folds were never found" from "the folds were found and the
        /// mapping is wrong", which is the difference between fixing the source and fixing the
        /// arithmetic. Bounded by the change check, so a session writes a handful of lines.
        /// </summary>
        private static void Report(
            bool hasManager,
            int folds,
            int hidden,
            int displayLines,
            int visibleLines,
            double track,
            double canvasHeight,
            List<MarkerRect> marks,
            double stripHeight)
        {
            var count = marks == null ? 0 : marks.Count;
            var firstTop = marks != null && count > 0 ? marks[0].Top : double.NaN;

            var signature = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}/{2}/{3}/{4}/{5:F0}/{6:F0}/{7}/{8:F0}/{9:F0}",
                hasManager ? 1 : 0,
                folds,
                hidden,
                displayLines,
                visibleLines,
                track,
                canvasHeight,
                count,
                firstTop,
                stripHeight);

            if (string.Equals(signature, _reported, StringComparison.Ordinal))
            {
                return;
            }

            _reported = signature;

            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TSVN");

                Directory.CreateDirectory(folder);

                File.AppendAllText(
                    Path.Combine(folder, "overview.log"),
                    "manager=" + (hasManager ? "yes" : "no")
                    + " folds=" + folds
                    + " hidden=" + hidden
                    + " displayLines=" + displayLines
                    + " visibleLines=" + visibleLines
                    + " track=" + track.ToString("F1", CultureInfo.InvariantCulture)
                    + " canvas=" + canvasHeight.ToString("F1", CultureInfo.InvariantCulture)
                    + " marks=" + count
                    + " first-top=" + firstTop.ToString("F1", CultureInfo.InvariantCulture)
                    + " strip=" + stripHeight.ToString("F1", CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + "  marks: " + Describe(marks)
                    + Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics are never a reason to break a margin.
            }
        }

        /// <summary>"top+height" of every mark, so the whole strip can be compared with a screenshot.</summary>
        private static string Describe(List<MarkerRect> marks)
        {
            var text = new System.Text.StringBuilder();

            if (marks != null)
            {
                for (var i = 0; i < marks.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append(marks[i].Top.ToString("F0", CultureInfo.InvariantCulture));
                    text.Append('+');
                    text.Append(marks[i].Height.ToString("F0", CultureInfo.InvariantCulture));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Height of one row of text, used to work out how much of the track the thumb takes.
        ///
        /// The view's own value, not the height of the first rendered line: a line long enough to
        /// wrap is two rows tall, and taking that as the row height halves the number of rows that
        /// are assumed to fit, which moves the whole map.
        /// </summary>
        private double GetLineHeight()
        {
            if (_textView.LineHeight > 0.0)
            {
                return _textView.LineHeight;
            }

            try
            {
                var lines = _textView.TextViewLines;

                if (lines != null && lines.IsValid && lines.Count > 0 && lines[0].Height > 0.0)
                {
                    return lines[0].Height;
                }
            }
            catch (Exception)
            {
                // Fall through to the last resort.
            }

            return 1.0;
        }
    }

    /// <summary>Places <see cref="DiffOverviewMargin"/> just left of the vertical scroll bar.</summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(DiffOverviewMargin.MarginName)]
    [Order(Before = PredefinedMarginNames.VerticalScrollBar)]
    [MarginContainer(PredefinedMarginNames.Right)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    public sealed class DiffOverviewMarginProvider : IWpfTextViewMarginProvider
    {
        /// <summary>
        /// Supplies the outline state, without which the marks cannot be placed where the scroll
        /// bar puts the thumb. Optional: without it the margin still works, just less precisely.
        /// </summary>
        [Import(AllowDefault = true)]
        public IOutliningManagerService OutliningManagerService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var textView = wpfTextViewHost.TextView;

            return new DiffOverviewMargin(textView, OutliningManagerService);
        }
    }
}
