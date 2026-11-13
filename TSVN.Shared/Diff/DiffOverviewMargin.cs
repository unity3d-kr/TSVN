using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
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

        private readonly IWpfTextView _textView;
        private readonly ITextBuffer _buffer;
        private readonly MarkerStrip _strip;
        private readonly IOutliningManager _outlining;

        private DiffPalette _palette;
        private bool _disposed;

        private List<DiffFolds.Range> _folds;
        private ITextSnapshot _foldsSnapshot;

        public DiffOverviewMargin(IWpfTextView textView, IOutliningManager outlining)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;
            _outlining = outlining;

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

            if (_outlining != null)
            {
                _outlining.RegionsChanged += OnRegionsChanged;
            }
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

            if (_outlining != null)
            {
                _outlining.RegionsChanged -= OnRegionsChanged;
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

        private void OnRegionsChanged(object sender, RegionsChangedEventArgs e)
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
                var canvasHeight = ViewScale.ToCanvas(height, scale);

                // The scroll bar measures the document in displayed lines: a collapsed region costs
                // nothing but its header.
                var displayLines = Math.Max(1, snapshot.LineCount - DiffFolds.HiddenTotal(folds));
                var visibleLines = Math.Max(1.0, Math.Round(canvasHeight / ViewScale.ToCanvas(GetLineHeight(), scale)));

                // The document is mapped onto the track minus the thumb, which is exactly the space
                // the scroll bar has to move the thumb through.
                var track = displayLines > visibleLines
                    ? Math.Max(1.0, canvasHeight * (1.0 - visibleLines / displayLines))
                    : canvasHeight;

                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    var startLine = chunk.StartLine - 1;
                    var endLine = chunk.EndLine - 1;

                    var displayStart = startLine - DiffFolds.HiddenBefore(folds, startLine);
                    var displayEnd = endLine - DiffFolds.HiddenBefore(folds, endLine);

                    var ratio = displayLines > 1 ? displayStart / (double)(displayLines - 1) : 0.0;
                    var top = ratio * track;
                    var markHeight = Math.Max(
                        MinimumMarkHeight,
                        (displayEnd - displayStart + 1) / (double)displayLines * track);

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
            _folds = DiffFolds.Collect(_outlining, snapshot);

            return _folds;
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
        /// <summary>
        /// Supplies the outline state, without which the marks cannot be placed where the scroll
        /// bar puts the thumb. Optional: without it the margin still works, just less precisely.
        /// </summary>
        [Import(AllowDefault = true)]
        public IOutliningManagerService OutliningManagerService { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var textView = wpfTextViewHost.TextView;
            IOutliningManager outlining = null;

            try
            {
                if (OutliningManagerService != null)
                {
                    outlining = OutliningManagerService.GetOutliningManager(textView);
                }
            }
            catch (Exception)
            {
                // No outlining support in this view; the margin falls back to buffer lines.
            }

            return new DiffOverviewMargin(textView, outlining);
        }
    }
}
