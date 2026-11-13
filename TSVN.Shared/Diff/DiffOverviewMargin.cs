using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
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
    ///
    /// A mark sits at its line's fraction of the document, which is the mapping the scroll bar
    /// itself uses: line n of a file of N lines is drawn at n / N of the bar. Collapsed outlining
    /// regions are deliberately not part of it. Folding does move everything below it, so a mark
    /// drifts by the height of the folds above it, but a bar this narrow is read as a map of the
    /// file rather than as a second scroll bar, and reading fold state on every paint bought
    /// nothing that survived the drift. The gutter is where exact positions matter, and it takes
    /// them from the rendered lines.
    /// </summary>
    public sealed class DiffOverviewMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffOverviewMargin";

        private const double StripWidth = 4.0;
        private const double MinimumMarkHeight = 2.0;

        private readonly IWpfTextView _textView;
        private readonly ITextBuffer _buffer;
        private readonly MarkerStrip _strip;

        private DiffPalette _palette;
        private bool _disposed;

        public DiffOverviewMargin(IWpfTextView textView)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;

            // Stretched rather than measured: the strip follows the height of the margin container,
            // which is the height the scroll bar is given, so a fraction of the strip is a fraction
            // of the bar with nothing to convert in between.
            _strip = new MarkerStrip(
                StripWidth,
                0.0,
                StripWidth,
                0.0,
                false,
                VerticalAlignment.Stretch);

            _textView.LayoutChanged += OnLayoutChanged;
            _textView.ViewportHeightChanged += OnViewportHeightChanged;
            _textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
            _textView.Closed += OnTextViewClosed;

            DiffChunkCache.Changed += OnChunksChanged;
            DiffMarkersState.Changed += OnStateChanged;
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
            _textView.ViewportHeightChanged -= OnViewportHeightChanged;
            _textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
            _textView.Closed -= OnTextViewClosed;

            DiffChunkCache.Changed -= OnChunksChanged;
            DiffMarkersState.Changed -= OnStateChanged;

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
            // The strip is stretched to the container, so a resize changes the height every mark is
            // a fraction of without a single line coordinate moving.
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
            // Scrolling raises this too, and moves nothing here: Apply rewrites only the rectangles
            // whose position or colour changed, and no line number did.
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

            var height = _strip.Element.ActualHeight;
            var lines = _textView.TextSnapshot.LineCount;

            if (chunks != null && chunks.Count > 0 && height > 0.0 && lines > 1)
            {
                if (_palette == null)
                {
                    _palette = DiffColorProvider.For(_textView.Background);
                }

                var palette = _palette;
                var perLine = height / lines;

                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    var startLine = chunk.StartLine - 1;
                    var endLine = chunk.EndLine - 1;

                    var top = startLine * perLine;
                    var markHeight = Math.Max(MinimumMarkHeight, (endLine - startLine + 1) * perLine);

                    if (top + markHeight > height)
                    {
                        top = Math.Max(0.0, height - markHeight);
                    }

                    marks.Add(new MarkerRect(
                        top,
                        markHeight,
                        palette.ForKind(chunk.Kind),
                        null));
                }
            }

            _strip.Apply(marks);
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
