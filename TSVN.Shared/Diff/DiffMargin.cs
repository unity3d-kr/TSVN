using System;
using System.Collections.Generic;
using System.Globalization;
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

        private readonly IWpfTextView _textView;
        private readonly Canvas _canvas;
        private readonly DiffMarkersController _controller;
        private readonly DispatcherTimer _retry;

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
            var markers = BuildMarkers(_controller == null ? null : _controller.Chunks, lines, _palette, viewportHeight);

            // A Canvas has no intrinsic size. Without an explicit height the margin container
            // arranges it at 0x0 and ClipToBounds throws every marker away, which looks exactly
            // like the margin was never created.
            _canvas.Width = GutterWidth;
            _canvas.Height = viewportHeight > 0 ? viewportHeight : 1.0;

            _canvas.Children.Clear();

            for (var i = 0; i < markers.Count; i++)
            {
                _canvas.Children.Add(markers[i]);
            }
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
            ITextViewLineCollection lines,
            DiffPalette palette,
            double viewportHeight)
        {
            var markers = new List<Rectangle>();

            if (chunks == null || chunks.Count == 0)
            {
                return markers;
            }

            // Only the rendered lines. A line hidden by a collapsed outlining region has no entry
            // here, and neither has a line outside the viewport, which is exactly what the two
            // branches below use to tell a fold apart from a run that scrolled away.
            var visible = BuildVisibleLines(lines);

            if (visible.Count == 0)
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
                        var foldBottom = visible[first - 1].Bottom;

                        markers.Add(CreateMarker(chunk, palette, foldBottom - CollapsedMarkHeight, CollapsedMarkHeight));
                    }

                    continue;
                }

                var index = first;

                while (index < visible.Count)
                {
                    // ITextViewLine.Top is the coordinate Visual Studio positions the line with,
                    // relative to the top of the viewport, so it is directly the canvas origin.
                    // No scroll offset participates in this computation.
                    var top = Math.Max(0.0, visible[index].Top);
                    var bottom = visible[index].Bottom;
                    var lineNumber = visible[index].LineNumber;
                    var next = index + 1;

                    // Extend over the following lines of the same run. A jump in the line numbers
                    // is a fold, so it starts a new stretch instead of one block spanning the fold.
                    while (next < visible.Count
                           && visible[next].LineNumber == lineNumber + 1
                           && visible[next].LineNumber <= endLine)
                    {
                        bottom = visible[next].Bottom;
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

                    markers.Add(CreateMarker(chunk, palette, top, bottom - top));

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
