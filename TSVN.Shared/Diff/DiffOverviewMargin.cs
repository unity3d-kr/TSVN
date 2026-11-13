using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
        private readonly Canvas _canvas;
        private readonly IOutliningManager _outlining;

        private DiffPalette _palette;
        private bool _disposed;

        private List<CollapsedRange> _collapsed;
        private ITextSnapshot _collapsedSnapshot;

        public DiffOverviewMargin(IWpfTextView textView, IOutliningManager outlining)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;
            _outlining = outlining;

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

            if (_outlining != null)
            {
                _outlining.RegionsChanged += OnRegionsChanged;
            }
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

            if (_outlining != null)
            {
                _outlining.RegionsChanged -= OnRegionsChanged;
            }

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

        private void OnRegionsChanged(object sender, RegionsChangedEventArgs e)
        {
            // A fold changes the displayed height of the document, so every mark moves.
            _collapsed = null;
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

            _canvas.Children.Clear();

            if (!DiffMarkersState.ShowOverviewBar || !DiffMarkersState.IsEnabled(DiffMargin.GetFilePath(_buffer)))
            {
                return;
            }

            var chunks = DiffChunkCache.Get(_buffer);
            var snapshot = _textView.TextSnapshot;
            var height = _textView.ViewportHeight;

            if (chunks.Count == 0 || height <= 0 || snapshot.LineCount <= 0)
            {
                return;
            }

            if (_palette == null)
            {
                _palette = DiffColorProvider.For(_textView.Background);
            }

            var palette = _palette;
            var collapsed = GetCollapsedRanges(snapshot);

            // The scroll bar measures the document in displayed lines: a collapsed region costs
            // nothing but its header. Counting buffer lines instead makes the marks drift as soon
            // as anything is folded.
            var hiddenTotal = 0;

            for (var i = 0; i < collapsed.Count; i++)
            {
                hiddenTotal += collapsed[i].Count;
            }

            var displayLines = Math.Max(1, snapshot.LineCount - hiddenTotal);
            var visibleLines = Math.Max(1.0, Math.Round(height / GetLineHeight()));

            // The scroll bar maps the document onto the track minus the thumb.
            var track = displayLines > visibleLines
                ? Math.Max(1.0, height * (1.0 - visibleLines / displayLines))
                : height;

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var startLine = chunk.StartLine - 1;
                var endLine = chunk.EndLine - 1;

                var displayStart = startLine - HiddenBefore(collapsed, startLine);
                var displayEnd = endLine - HiddenBefore(collapsed, endLine);

                var ratio = displayLines > 1 ? displayStart / (double)(displayLines - 1) : 0.0;
                var top = ratio * track;
                var markHeight = Math.Max(MinimumMarkHeight, (displayEnd - displayStart + 1) / (double)displayLines * track);

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

        /// <summary>
        /// Buffer lines hidden by collapsed outlining regions, merged so overlapping folds are not
        /// counted twice.
        /// </summary>
        private List<CollapsedRange> GetCollapsedRanges(ITextSnapshot snapshot)
        {
            if (_collapsed != null && ReferenceEquals(_collapsedSnapshot, snapshot))
            {
                return _collapsed;
            }

            var ranges = new List<CollapsedRange>();
            _collapsedSnapshot = snapshot;

            try
            {
                if (_outlining != null)
                {
                    foreach (var region in _outlining.GetCollapsedRegions(new SnapshotSpan(snapshot, 0, snapshot.Length)))
                    {
                        var extent = region.Extent.GetSpan(snapshot);
                        var first = extent.Start.GetContainingLine().LineNumber;
                        var last = extent.End.GetContainingLine().LineNumber;

                        // The header line of a fold stays on screen.
                        if (last > first)
                        {
                            ranges.Add(new CollapsedRange(first + 1, last));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Outlining can be unavailable while the view is being torn down; ignoring it only
                // costs accuracy, never the marks.
            }

            ranges.Sort((left, right) => left.First.CompareTo(right.First));

            var merged = new List<CollapsedRange>();

            for (var i = 0; i < ranges.Count; i++)
            {
                if (merged.Count > 0 && ranges[i].First <= merged[merged.Count - 1].Last + 1)
                {
                    var previous = merged[merged.Count - 1];
                    previous.Last = Math.Max(previous.Last, ranges[i].Last);
                    merged[merged.Count - 1] = previous;

                    continue;
                }

                merged.Add(ranges[i]);
            }

            _collapsed = merged;

            return merged;
        }

        /// <summary>How many of the given lines are hidden by a fold above or at <paramref name="line"/>.</summary>
        private static int HiddenBefore(List<CollapsedRange> ranges, int line)
        {
            var hidden = 0;

            for (var i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];

                if (line < range.First)
                {
                    break;
                }

                hidden += Math.Min(line, range.Last) - range.First + 1;
            }

            return hidden;
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

        private struct CollapsedRange
        {
            public CollapsedRange(int first, int last)
            {
                First = first;
                Last = last;
            }

            public int First;

            public int Last;

            public int Count
            {
                get { return Last - First + 1; }
            }
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
