using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
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
    /// Positions are not computed here at all. They are asked of the editor's own vertical scroll
    /// bar through <see cref="IVerticalScrollBar.GetYCoordinateOfBufferPosition"/>, the function
    /// that places its thumb. Collapsed outlining regions, the track between the arrow buttons,
    /// the room the thumb takes and whatever mode the bar is in are all already part of that
    /// mapping, so a mark lines up with the thumb by construction instead of by a model of the
    /// scroll bar kept in sync with it. Two earlier versions modelled the folds themselves from
    /// the outlining manager; both drifted, because the model was never quite the bar's.
    ///
    /// A folded change maps to the fold's position and comes out zero pixels tall, so it is drawn
    /// at the minimum height there: the change is still visible, at the place the fold is.
    /// </summary>
    public sealed class DiffOverviewMargin : IWpfTextViewMargin
    {
        public const string MarginName = "TsvnDiffOverviewMargin";

        private const double StripWidth = 4.0;
        private const double MinimumMarkHeight = 2.0;

        private readonly IWpfTextView _textView;
        private readonly IWpfTextViewMargin _container;
        private readonly ITextBuffer _buffer;
        private readonly MarkerStrip _strip;

        private IVerticalScrollBar _scrollBar;
        private IScrollMap _scrollMap;
        private DiffPalette _palette;
        private bool _disposed;

        public DiffOverviewMargin(IWpfTextView textView, IWpfTextViewMargin container)
        {
            _textView = textView;
            _container = container;
            _buffer = textView.TextBuffer;

            // Stretched to the margin container, which is the height the scroll bar is given too.
            _strip = new MarkerStrip(
                StripWidth,
                0.0,
                StripWidth,
                0.0,
                false,
                VerticalAlignment.Stretch);

            _strip.Element.SizeChanged += OnStripSizeChanged;

            _textView.LayoutChanged += OnLayoutChanged;
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

            _strip.Element.SizeChanged -= OnStripSizeChanged;

            _textView.LayoutChanged -= OnLayoutChanged;
            _textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
            _textView.Closed -= OnTextViewClosed;

            DiffChunkCache.Changed -= OnChunksChanged;
            DiffMarkersState.Changed -= OnStateChanged;

            if (_scrollBar != null)
            {
                _scrollBar.TrackSpanChanged -= OnScrollBarChanged;
            }

            if (_scrollMap != null)
            {
                _scrollMap.MappingChanged -= OnScrollBarChanged;
            }

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

        private void OnStripSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.HeightChanged)
            {
                Render();
            }
        }

        private void OnScrollBarChanged(object sender, EventArgs e)
        {
            // The track moved or resized, or the map changed: a fold, an edit, a mode switch.
            Render();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            Render();
        }

        private void OnChunksChanged(object sender, ITextBuffer buffer)
        {
            if (ReferenceEquals(buffer, _buffer))
            {
                Render();
            }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // Scrolling is by far the most frequent layout and moves no mark, so it is skipped.
            // What does move them: the text (edit snapshot), the folds (visual snapshot - the
            // elision buffer gets a new snapshot whenever a region collapses or expands), and the
            // first layout, before which the scroll bar does not exist yet.
            if (_scrollBar != null
                && ReferenceEquals(e.OldViewState.EditSnapshot, e.NewViewState.EditSnapshot)
                && ReferenceEquals(e.OldViewState.VisualSnapshot, e.NewViewState.VisualSnapshot))
            {
                return;
            }

            Render();
        }

        /// <summary>
        /// The editor's vertical scroll bar, found through the margin container this strip lives in.
        ///
        /// Looked up on demand: this margin is ordered before the scroll bar, so the container
        /// creates it first and the scroll bar does not exist yet when the constructor runs.
        /// </summary>
        private IVerticalScrollBar ResolveScrollBar()
        {
            if (_scrollBar != null || _container == null)
            {
                return _scrollBar;
            }

            var scrollBar = _container.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar) as IVerticalScrollBar;

            if (scrollBar == null)
            {
                return null;
            }

            _scrollBar = scrollBar;
            _scrollBar.TrackSpanChanged += OnScrollBarChanged;

            _scrollMap = scrollBar.Map;

            if (_scrollMap != null)
            {
                _scrollMap.MappingChanged += OnScrollBarChanged;
            }

            return _scrollBar;
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
            var snapshot = _textView.TextSnapshot;

            if (chunks != null && chunks.Count > 0 && height > 0.0 && snapshot.LineCount > 0)
            {
                if (_palette == null)
                {
                    _palette = DiffColorProvider.For(_textView.Background);
                }

                var scrollBar = ResolveScrollBar();

                if (scrollBar != null)
                {
                    AddMappedMarks(marks, chunks, snapshot, scrollBar, height);
                }
                else
                {
                    AddProportionalMarks(marks, chunks, snapshot.LineCount, height);
                }
            }

            _strip.Apply(marks);
        }

        /// <summary>Every mark placed where the scroll bar puts the same buffer position.</summary>
        private void AddMappedMarks(
            List<MarkerRect> marks,
            IReadOnlyList<DiffChunk> chunks,
            ITextSnapshot snapshot,
            IVerticalScrollBar scrollBar,
            double height)
        {
            // The scroll bar answers in its own coordinates. Both are children of the same
            // container, so this is normally a no-op, but one transform per paint keeps the marks
            // right if the bar is ever offset or scaled relative to the strip.
            var toStrip = TransformToStrip(scrollBar);
            var lastLine = snapshot.LineCount - 1;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                // The chunks can trail the snapshot by one edit; a line past the end is clamped
                // rather than thrown on, and the next chunk update corrects it.
                var startLine = Math.Min(Math.Max(chunk.StartLine - 1, 0), lastLine);
                var endLine = Math.Min(Math.Max(chunk.EndLine - 1, startLine), lastLine);

                var start = snapshot.GetLineFromLineNumber(startLine).Start;
                var end = snapshot.GetLineFromLineNumber(endLine).EndIncludingLineBreak;

                var top = ToStrip(toStrip, scrollBar.GetYCoordinateOfBufferPosition(start));
                var bottom = ToStrip(toStrip, scrollBar.GetYCoordinateOfBufferPosition(end));

                AddMark(marks, chunk, top, bottom, height);
            }
        }

        /// <summary>Fallback for a view without a vertical scroll bar: the line's fraction of the file.</summary>
        private void AddProportionalMarks(List<MarkerRect> marks, IReadOnlyList<DiffChunk> chunks, int lineCount, double height)
        {
            var perLine = height / lineCount;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                AddMark(marks, chunk, (chunk.StartLine - 1) * perLine, chunk.EndLine * perLine, height);
            }
        }

        private void AddMark(List<MarkerRect> marks, DiffChunk chunk, double top, double bottom, double height)
        {
            var markHeight = Math.Max(MinimumMarkHeight, bottom - top);

            if (top + markHeight > height)
            {
                top = height - markHeight;
            }

            top = Math.Max(0.0, top);

            marks.Add(new MarkerRect(top, markHeight, _palette.ForKind(chunk.Kind), null));
        }

        private GeneralTransform TransformToStrip(IVerticalScrollBar scrollBar)
        {
            var margin = scrollBar as IWpfTextViewMargin;

            if (margin == null || margin.VisualElement == null)
            {
                return null;
            }

            try
            {
                return margin.VisualElement.TransformToVisual(_strip.Element);
            }
            catch (InvalidOperationException)
            {
                // Not in the same visual tree yet; the bar's own coordinates are the best guess.
                return null;
            }
        }

        private static double ToStrip(GeneralTransform transform, double y)
        {
            return transform == null ? y : transform.Transform(new Point(0.0, y)).Y;
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
            return new DiffOverviewMargin(wpfTextViewHost.TextView, marginContainer);
        }
    }
}
