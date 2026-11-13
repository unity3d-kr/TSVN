using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// A four pixel map of the whole file next to the vertical scroll bar, so changed regions
    /// stay visible when they are scrolled out of view.
    ///
    /// It is a passive view over <see cref="DiffChunkCache"/>: the gutter margin owns the
    /// controller, this one only paints what it finds there.
    /// </summary>
    public sealed class DiffOverviewMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffOverviewMargin";

        private const double StripWidth = 4.0;
        private const double MinimumMarkHeight = 2.0;

        private readonly IWpfTextView _textView;
        private readonly ITextBuffer _buffer;
        private readonly Canvas _canvas;

        private DiffPalette _palette;
        private bool _disposed;

        public DiffOverviewMargin(IWpfTextView textView)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;

            _canvas = new Canvas
            {
                Width = StripWidth,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };

            _textView.LayoutChanged += OnLayoutChanged;
            _textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
            _textView.Closed += OnTextViewClosed;

            DiffChunkCache.Changed += OnChunksChanged;
            DiffMarkersState.Changed += OnStateChanged;
        }

        public event EventHandler Disposed;

        public FrameworkElement VisualElement
        {
            get { return _canvas; }
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
            _textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
            _textView.Closed -= OnTextViewClosed;

            DiffChunkCache.Changed -= OnChunksChanged;
            DiffMarkersState.Changed -= OnStateChanged;

            _canvas.Children.Clear();

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

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // Only a resize changes the mapping from line number to pixels.
            if (e.OldViewState.ViewportHeight != e.NewViewState.ViewportHeight)
            {
                Render();
            }
        }

        private void Render()
        {
            if (_disposed)
            {
                return;
            }

            _canvas.Children.Clear();

            if (!DiffMarkersState.ShowOverviewBar || !DiffMarkersState.IsEnabled(DiffMargin.GetFilePath(_buffer)))
            {
                return;
            }

            var chunks = DiffChunkCache.Get(_buffer);
            var total = _textView.TextSnapshot.LineCount;
            var height = _textView.ViewportHeight;

            if (chunks.Count == 0 || total <= 0 || height <= 0)
            {
                return;
            }

            if (_palette == null)
            {
                _palette = DiffColorProvider.For(_textView.Background);
            }

            var palette = _palette;

            // The vertical scroll bar maps the document onto the track minus the thumb, not onto
            // the whole viewport. Spreading the line numbers over the full height therefore drifts
            // away from the thumb the further down the file a change sits. Use the scroll bar's
            // own mapping so a mark sits where the thumb would be for that line.
            var lineHeight = GetLineHeight();
            var visibleLines = Math.Max(1.0, Math.Round(height / lineHeight));
            var track = total > visibleLines
                ? Math.Max(1.0, height * (1.0 - visibleLines / total))
                : height;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var ratio = total > 1 ? (chunk.StartLine - 1) / (double)(total - 1) : 0.0;
                var top = ratio * track;
                var markHeight = chunk.LineCount / (double)total * track;

                if (markHeight < MinimumMarkHeight)
                {
                    markHeight = MinimumMarkHeight;
                }

                if (top + markHeight > height)
                {
                    top = Math.Max(0, height - markHeight);
                }

                var mark = new Rectangle
                {
                    Width = StripWidth,
                    Height = markHeight,
                    Fill = palette.ForKind(chunk.Kind)
                };

                Canvas.SetLeft(mark, 0);
                Canvas.SetTop(mark, top);
                _canvas.Children.Add(mark);
            }
        }

        /// <summary>Height of one rendered line, used to work out how much of the track the thumb takes.</summary>
        private double GetLineHeight()
        {
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
                // Fall through to the view level value.
            }

            return _textView.LineHeight > 0.0 ? _textView.LineHeight : 1.0;
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
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            return new DiffOverviewMargin(wpfTextViewHost.TextView);
        }
    }
}
