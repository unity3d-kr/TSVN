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
                VerticalAlignment = VerticalAlignment.Stretch,
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
            var firstVisible = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            var lastVisible = lines.LastVisibleLine.End.GetContainingLine().LineNumber;
            var spans = BuildVisibleSpans(lines);
            var viewportTop = _textView.ViewportTop;
            var viewportBottom = viewportTop + _textView.ViewportHeight;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var start = chunk.StartLine - 1;
                var end = chunk.EndLine - 1;

                if (end < firstVisible || start > lastVisible)
                {
                    continue;
                }

                var clampedStart = start < firstVisible ? firstVisible : start;
                var clampedEnd = end > lastVisible ? lastVisible : end;

                double top;
                double bottom;

                if (start < firstVisible)
                {
                    top = viewportTop;
                }
                else
                {
                    LineSpan span;

                    if (!spans.TryGetValue(clampedStart, out span))
                    {
                        continue;
                    }

                    top = span.Top;
                }

                if (end > lastVisible)
                {
                    bottom = viewportBottom;
                }
                else
                {
                    LineSpan span;

                    if (!spans.TryGetValue(clampedEnd, out span))
                    {
                        continue;
                    }

                    bottom = span.Bottom;
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
        /// Vertical extent of each visible buffer line. A wrapped line produces several text
        /// view lines, so the extents are merged.
        /// </summary>
        private static Dictionary<int, LineSpan> BuildVisibleSpans(ITextViewLineCollection lines)
        {
            var spans = new Dictionary<int, LineSpan>();

            foreach (var line in lines)
            {
                var number = line.Start.GetContainingLine().LineNumber;
                LineSpan existing;

                if (spans.TryGetValue(number, out existing))
                {
                    var top = Math.Min(existing.Top, line.Top);
                    var bottom = Math.Max(existing.Bottom, line.Bottom);
                    spans[number] = new LineSpan(top, bottom);
                }
                else
                {
                    spans[number] = new LineSpan(line.Top, line.Bottom);
                }
            }

            return spans;
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
            public LineSpan(double top, double bottom)
            {
                Top = top;
                Bottom = bottom;
            }

            public double Top { get; private set; }

            public double Bottom { get; private set; }
        }
    }
}
