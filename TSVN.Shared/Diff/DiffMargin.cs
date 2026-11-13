using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

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
    /// </summary>
    public sealed class DiffMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffMargin";

        private const double GutterWidth = 6.0;
        private const double MarkerLeft = 1.0;
        private const double MarkerWidth = GutterWidth - 2.0;

        /// <summary>Height of the stub used when a changed run is hidden inside a folded region.</summary>
        private const double CollapsedMarkHeight = 3.0;

        /// <summary>
        /// How often a render may be retried while the view has no usable layout. A chunk list
        /// can arrive before the first layout pass, and without a retry the strip stays empty
        /// until the user scrolls.
        /// </summary>
        private const int MaxRetryCount = 8;

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

        /// <summary>How many diagnostic lines one session may write; see <see cref="Trace"/>.</summary>
        private const int TraceLimit = 300;

        /// <summary>Largest container offset treated as a real displacement; see <see cref="ResolveContainerOffset"/>.</summary>
        private const double MaxContainerOffset = 50.0;

        private static int _traceCount;

        private readonly IWpfTextView _textView;
        private readonly Canvas _canvas;
        private readonly DiffMarkersController _controller;
        private readonly DispatcherTimer _retry;
        private readonly string _filePath;

        private DiffPalette _palette;
        private bool _disposed;
        private int _retryCount;

        public DiffMargin(IWpfTextView textView)
        {
            _textView = textView;

            _canvas = new Canvas
            {
                Width = GutterWidth,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = true
            };

            _retry = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = RetryDelay
            };

            _retry.Tick += OnRetryTick;

            _textView.LayoutChanged += OnLayoutChanged;
            _textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
            _textView.Closed += OnTextViewClosed;

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

            Trace(string.Format(
                CultureInfo.InvariantCulture,
                "margin created path=\"{0}\" controller={1}",
                filePath ?? "<none>",
                _controller == null ? "no" : "yes"));
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
            _textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
            _textView.Closed -= OnTextViewClosed;

            _retry.Stop();
            _retry.Tick -= OnRetryTick;

            if (_controller != null)
            {
                _controller.ChunksChanged -= OnChunksChanged;
                _controller.Dispose();
            }

            _canvas.Children.Clear();

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

        private void OnChunksChanged(object sender, EventArgs e)
        {
            // Logged separately: a chunk list can arrive while the layout is invalid, in which
            // case Render never reaches its own trace.
            Trace(string.Format(
                CultureInfo.InvariantCulture,
                "chunks changed \"{0}\" n={1}",
                _filePath ?? "<none>",
                _controller == null || _controller.Chunks == null
                    ? "null"
                    : _controller.Chunks.Count.ToString(CultureInfo.InvariantCulture)));

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

            if (_palette == null)
            {
                _palette = DiffColorProvider.For(_textView.Background);
            }

            var viewportHeight = _textView.ViewportHeight;
            var chunks = _controller == null ? null : _controller.Chunks;

            // Only the rendered lines. A line hidden by a collapsed outlining region has no entry
            // here, and neither has a line outside the viewport, which is what the marker builder
            // uses to tell a fold apart from a run that scrolled away.
            var visible = BuildVisibleLines(lines);

            // A Canvas has no intrinsic size. Without an explicit height the margin container
            // arranges it at 0x0 and ClipToBounds throws every marker away, which looks exactly
            // like the margin was never created.
            _canvas.Width = GutterWidth;
            _canvas.Height = viewportHeight > 0 ? viewportHeight : 1.0;

            var origin = ResolveOrigin(visible, _textView.ViewportTop, viewportHeight);
            var shift = ResolveContainerOffset() - origin;
            var markers = chunks == null || chunks.Count == 0 || visible.Count == 0
                ? new List<Rectangle>()
                : BuildMarkers(chunks, visible, _palette, viewportHeight, shift);

            // Auxiliary views (peek, preview) render at zero height and would otherwise use up the
            // whole budget before the document view ever logs.
            if (viewportHeight > 0.0 || _traceCount < 10)
            {
                Trace(string.Format(
                    CultureInfo.InvariantCulture,
                    "render \"{0}\" controller={1} enabled={2} chunks={3} visible={4} origin={5} shift={6} vpTop={7} vpHeight={8} firstLine={9} firstTop={10} markers={11}",
                    _filePath ?? "<none>",
                    _controller == null ? "no" : "yes",
                    DiffMarkersState.IsEnabled(_filePath) ? "yes" : "no",
                    chunks == null ? "null" : chunks.Count.ToString(CultureInfo.InvariantCulture),
                    visible.Count,
                    origin,
                    shift,
                    _textView.ViewportTop,
                    viewportHeight,
                    visible.Count > 0 ? visible[0].LineNumber : -1,
                    visible.Count > 0 ? visible[0].Top : double.NaN,
                    markers.Count));
            }

            _canvas.Children.Clear();

            for (var i = 0; i < markers.Count; i++)
            {
                _canvas.Children.Add(markers[i]);
            }
        }

        /// <summary>
        /// Decides which origin the line coordinates are measured from.
        ///
        /// The documented contract says <see cref="ITextViewLine.Top"/> is relative to the top of
        /// the viewport, but the WPF editor lays its lines out inside a scrolled canvas, where the
        /// same number is relative to the document instead. Getting this wrong puts every marker
        /// at a document-sized offset, which simply looks like nothing was drawn.
        ///
        /// Rather than guess from a single line - the editor also renders a line or two beyond
        /// each edge, which breaks any "the first line must be at zero" check - both candidate
        /// origins are tried and the one that leaves more rendered lines inside the viewport wins.
        /// </summary>
        private static double ResolveOrigin(IReadOnlyList<LineSpan> visible, double viewportTop, double viewportHeight)
        {
            if (visible.Count == 0 || viewportTop == 0.0 || viewportHeight <= 0.0)
            {
                return 0.0;
            }

            var viewportRelative = CountLinesInViewport(visible, 0.0, viewportHeight);

            if (viewportRelative == visible.Count)
            {
                return 0.0; // already consistent with the top of the viewport
            }

            var documentRelative = CountLinesInViewport(visible, viewportTop, viewportHeight);

            return documentRelative > viewportRelative ? viewportTop : 0.0;
        }

        /// <summary>How many rendered lines would fall inside the viewport with the given origin.</summary>
        private static int CountLinesInViewport(IReadOnlyList<LineSpan> visible, double origin, double viewportHeight)
        {
            var count = 0;

            for (var i = 0; i < visible.Count; i++)
            {
                var top = visible[i].Top - origin;
                var bottom = visible[i].Bottom - origin;

                if (bottom > 0.0 && top < viewportHeight)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Vertical distance between the top of the text view and the top of the gutter canvas.
        ///
        /// Line coordinates are measured from the text view, while the canvas is arranged by the
        /// margin container, and those two origins need not coincide - a padding or a one pixel
        /// border is enough to put every marker a few pixels away from the line it belongs to.
        /// Measuring the distance removes the guesswork.
        ///
        /// Clamped, because a scroll transform on the text view would turn the difference into a
        /// document sized value; that translation is already handled by the origin.
        /// </summary>
        private double ResolveContainerOffset()
        {
            try
            {
                var textElement = _textView.VisualElement;

                if (textElement == null || _canvas == null)
                {
                    return 0.0;
                }

                var ancestor = FindCommonAncestor(textElement, _canvas);

                if (ancestor == null)
                {
                    return 0.0;
                }

                var textTop = textElement.TransformToAncestor(ancestor).Transform(new Point(0.0, 0.0)).Y;
                var canvasTop = _canvas.TransformToAncestor(ancestor).Transform(new Point(0.0, 0.0)).Y;
                var offset = textTop - canvasTop;

                return Math.Abs(offset) <= MaxContainerOffset ? offset : 0.0;
            }
            catch (Exception)
            {
                // Never let a visual tree quirk take the markers out entirely.
                return 0.0;
            }
        }

        private static Visual FindCommonAncestor(DependencyObject first, DependencyObject second)
        {
            var ancestors = new HashSet<DependencyObject>();

            for (var current = first; current != null; current = VisualTreeHelper.GetParent(current))
            {
                ancestors.Add(current);
            }

            for (var current = second; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (ancestors.Contains(current))
                {
                    return current as Visual;
                }
            }

            return null;
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
        /// </summary>
        private static List<Rectangle> BuildMarkers(
            IReadOnlyList<DiffChunk> chunks,
            IReadOnlyList<LineSpan> visible,
            DiffPalette palette,
            double viewportHeight,
            double shift)
        {
            var markers = new List<Rectangle>();

            if (chunks == null || chunks.Count == 0 || visible.Count == 0)
            {
                return markers;
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var startLine = chunk.StartLine - 1;
                var endLine = chunk.EndLine - 1;

                var first = FirstAtOrAfter(visible, startLine);

                if (first == visible.Count || visible[first].LineNumber > endLine)
                {
                    // Nothing rendered inside the run. Rendered lines on both sides mean a fold;
                    // a run above the first or below the last rendered line is simply off screen.
                    if (first > 0 && first < visible.Count && visible[first - 1].LineNumber < startLine)
                    {
                        var foldBottom = visible[first - 1].Bottom + shift;

                        markers.Add(CreateMarker(chunk, palette, foldBottom - CollapsedMarkHeight, CollapsedMarkHeight));
                    }

                    continue;
                }

                var index = first;

                while (index < visible.Count)
                {
                    // The line coordinates are moved onto the canvas by the shift resolved for this
                    // layout; see ResolveOrigin and ResolveContainerOffset.
                    var top = Math.Max(0.0, visible[index].Top + shift);
                    var bottom = visible[index].Bottom + shift;
                    var lineNumber = visible[index].LineNumber;
                    var next = index + 1;

                    // Extend over the following lines of the same run. A jump in the line numbers
                    // is a fold, so it starts a new stretch instead of one block spanning the fold.
                    while (next < visible.Count
                           && visible[next].LineNumber == lineNumber + 1
                           && visible[next].LineNumber <= endLine)
                    {
                        bottom = visible[next].Bottom + shift;
                        lineNumber = visible[next].LineNumber;
                        next++;
                    }

                    if (viewportHeight > 0)
                    {
                        // A run that continues past the last rendered line fills the view down to
                        // its bottom edge; one that ends on screen stops at its last line.
                        var runsOffScreen = next == visible.Count && lineNumber < endLine;

                        bottom = runsOffScreen
                            ? Math.Max(bottom, viewportHeight)
                            : Math.Min(bottom, viewportHeight);
                    }

                    var offScreen = viewportHeight > 0 && (bottom <= 0.0 || top >= viewportHeight);

                    if (!offScreen)
                    {
                        markers.Add(CreateMarker(chunk, palette, top, bottom - top));
                    }

                    if (next >= visible.Count || visible[next].LineNumber > endLine)
                    {
                        break;
                    }

                    index = next;
                }
            }

            return markers;
        }

        private static Rectangle CreateMarker(DiffChunk chunk, DiffPalette palette, double top, double height)
        {
            var markerHeight = height;

            if (markerHeight < 1.0)
            {
                markerHeight = 1.0;
            }
            else if (markerHeight > 2.0)
            {
                markerHeight -= 1.0; // one pixel gap keeps adjacent runs distinguishable
            }

            var marker = new Rectangle
            {
                Width = MarkerWidth,
                Height = markerHeight,
                Fill = palette.ForKind(chunk.Kind),
                RadiusX = 1.5,
                RadiusY = 1.5,
                ToolTip = Describe(chunk)
            };

            Canvas.SetLeft(marker, MarkerLeft);
            Canvas.SetTop(marker, top);

            return marker;
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

        /// <summary>
        /// Capped trace of the render inputs, written to %LOCALAPPDATA%\TSVN\diff-markers.log.
        /// Temporary: it exists to confirm which coordinate space Visual Studio reports on a
        /// machine that runs a real editor, then it goes away.
        /// </summary>
        private static void Trace(string message)
        {
            if (Interlocked.Increment(ref _traceCount) > TraceLimit)
            {
                return;
            }

            try
            {
                var folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TSVN");

                Directory.CreateDirectory(folder);

                File.AppendAllText(System.IO.Path.Combine(folder, "diff-markers.log"), message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics are never a reason to break the margin.
            }
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
