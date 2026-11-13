using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
    /// </summary>
    public sealed class DiffMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffMargin";

        private const double GutterWidth = 6.0;
        private const double MarkerLeft = 1.0;
        private const double MarkerWidth = GutterWidth - 2.0;

        /// <summary>Height of the stub used when a changed run is hidden inside a folded region.</summary>
        private const double CollapsedMarkHeight = 3.0;

        private readonly IWpfTextView _textView;
        private readonly Canvas _canvas;
        private readonly DiffMarkersController _controller;

        private DiffPalette _palette;
        private bool _disposed;

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
            // Positions are recomputed from the current layout, so only a full redraw of the
            // markers matters here; the chunk list itself is unaffected by scrolling.
            Render();
        }

        private void Render()
        {
            if (_disposed)
            {
                return;
            }

            _canvas.Children.Clear();

            // A Canvas has no intrinsic size. Without an explicit height the margin container
            // arranges it at 0x0 and ClipToBounds throws every marker away, which looks exactly
            // like the margin was never created.
            _canvas.Width = GutterWidth;
            _canvas.Height = _textView.ViewportHeight > 0 ? _textView.ViewportHeight : 1.0;

            var chunks = _controller == null ? null : _controller.Chunks;

            if (chunks == null || chunks.Count == 0)
            {
                return;
            }

            var lines = _textView.TextViewLines;

            if (lines == null || !lines.IsValid)
            {
                return;
            }

            if (_palette == null)
            {
                _palette = DiffColorProvider.For(_textView.Background);
            }

            var palette = _palette;

            // Only the lines that are actually rendered. Lines hidden by a collapsed outlining
            // region simply have no entry, which is how a chunk living inside the fold is told
            // apart from one that is merely scrolled out of view.
            var visible = BuildVisibleLines(lines);

            if (visible.Count == 0)
            {
                return;
            }

            // ITextViewLine.Top is already relative to the top of the viewport (the same space
            // GetTextViewLineContainingYCoordinate uses), which is exactly the canvas origin.
            // Do not add or subtract the scroll offset here: ViewportTop is a layout-space
            // value and using it shifts every marker off the top of the gutter.
            var viewportHeight = _textView.ViewportHeight;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var start = chunk.StartLine - 1;
                var end = chunk.EndLine - 1;

                var first = FirstAtOrAfter(visible, start);

                if (first < 0)
                {
                    continue; // the whole run sits below the last rendered line
                }

                double top;
                double bottom;

                if (visible[first].LineNumber > end)
                {
                    // The run is entirely inside a collapsed region. Point at the fold rather
                    // than stretching one marker across everything below it.
                    var fold = first - 1;

                    if (fold < 0)
                    {
                        continue;
                    }

                    bottom = visible[fold].Bottom;
                    top = bottom - CollapsedMarkHeight;
                }
                else
                {
                    top = visible[first].Top;

                    if (top < 0.0)
                    {
                        top = 0.0; // the first line is partially scrolled out
                    }

                    var last = LastAtOrBefore(visible, end, first);
                    var runsPastBottom = visible[visible.Count - 1].LineNumber < end;

                    bottom = runsPastBottom ? viewportHeight : visible[last].Bottom;
                }

                if (bottom <= top)
                {
                    bottom = top + 2.0;
                }

                var height = bottom - top;

                if (height < 1.0)
                {
                    height = 1.0;
                }
                else if (height > 2.0)
                {
                    height -= 1.0; // leave a one pixel gap so adjacent runs stay distinguishable
                }

                var marker = new Rectangle
                {
                    Width = MarkerWidth,
                    Height = height,
                    Fill = palette.ForKind(chunk.Kind),
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    ToolTip = Describe(chunk)
                };

                Canvas.SetLeft(marker, MarkerLeft);
                Canvas.SetTop(marker, top);
                _canvas.Children.Add(marker);
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

        /// <summary>Index of the first rendered line at or after <paramref name="lineNumber"/>, or -1.</summary>
        private static int FirstAtOrAfter(IReadOnlyList<LineSpan> visible, int lineNumber)
        {
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i].LineNumber >= lineNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Index of the last rendered line at or before <paramref name="lineNumber"/>.</summary>
        private static int LastAtOrBefore(IReadOnlyList<LineSpan> visible, int lineNumber, int from)
        {
            var result = from;

            for (var i = from; i < visible.Count && visible[i].LineNumber <= lineNumber; i++)
            {
                result = i;
            }

            return result;
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
