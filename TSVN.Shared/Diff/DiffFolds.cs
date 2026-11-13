using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Outlining;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// The buffer lines that collapsed outlining regions hide, merged into one ordered list.
    ///
    /// A fold keeps its header line on screen and hides everything after it, which is all the two
    /// margins need to know: the gutter marks the header in place of the hidden lines, and the
    /// scroll bar subtracts the hidden lines from the document height before mapping a line onto
    /// the track.
    ///
    /// Nested folds are merged into their outermost region and keep that region's header, because
    /// a nested header is itself hidden and therefore is not a line the gutter can draw on.
    /// </summary>
    public static class DiffFolds
    {
        /// <summary>Shared empty result; callers must not modify it.</summary>
        public static readonly List<Range> None = new List<Range>();

        /// <summary>
        /// Collapsed regions of <paramref name="snapshot"/>, ordered by their first hidden line.
        /// Returns <see cref="None"/> when outlining is unavailable, so callers never have to
        /// null check the result.
        /// </summary>
        public static List<Range> Collect(IOutliningManager outlining, ITextSnapshot snapshot)
        {
            if (outlining == null || snapshot == null)
            {
                return None;
            }

            var ranges = new List<Range>();

            try
            {
                foreach (var region in outlining.GetCollapsedRegions(new SnapshotSpan(snapshot, 0, snapshot.Length)))
                {
                    var extent = region.Extent.GetSpan(snapshot);
                    var header = extent.Start.GetContainingLine().LineNumber;
                    var last = extent.End.GetContainingLine().LineNumber;

                    // A region that hides nothing, and the header line, which stays on screen.
                    if (last > header)
                    {
                        ranges.Add(new Range(header, header + 1, last));
                    }
                }
            }
            catch (Exception)
            {
                // Outlining is unavailable while a view is being torn down, and a projection view
                // may not support it at all. Losing the fold information only costs accuracy.
                return None;
            }

            if (ranges.Count == 0)
            {
                return None;
            }

            ranges.Sort((left, right) => left.First.CompareTo(right.First));

            var merged = new List<Range>();

            for (var i = 0; i < ranges.Count; i++)
            {
                if (merged.Count > 0 && ranges[i].First <= merged[merged.Count - 1].Last + 1)
                {
                    var previous = merged[merged.Count - 1];

                    previous.Last = Math.Max(previous.Last, ranges[i].Last);
                    previous.Header = Math.Min(previous.Header, ranges[i].Header);
                    merged[merged.Count - 1] = previous;

                    continue;
                }

                merged.Add(ranges[i]);
            }

            return merged;
        }

        /// <summary>
        /// Header line of the region hiding <paramref name="line"/>, or -1 when no region does.
        /// </summary>
        public static int HeaderFor(List<Range> ranges, int line)
        {
            if (ranges == null)
            {
                return -1;
            }

            for (var i = 0; i < ranges.Count; i++)
            {
                if (line < ranges[i].First)
                {
                    break;
                }

                if (line <= ranges[i].Last)
                {
                    return ranges[i].Header;
                }
            }

            return -1;
        }

        /// <summary>How many lines a fold hides above or at <paramref name="line"/>.</summary>
        public static int HiddenBefore(List<Range> ranges, int line)
        {
            if (ranges == null)
            {
                return 0;
            }

            var hidden = 0;

            for (var i = 0; i < ranges.Count; i++)
            {
                if (line < ranges[i].First)
                {
                    break;
                }

                hidden += Math.Min(line, ranges[i].Last) - ranges[i].First + 1;
            }

            return hidden;
        }

        /// <summary>Total number of hidden lines, i.e. how much shorter the document is than its line count.</summary>
        public static int HiddenTotal(List<Range> ranges)
        {
            var hidden = 0;

            if (ranges != null)
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    hidden += ranges[i].Count;
                }
            }

            return hidden;
        }

        /// <summary>One collapsed region: its header line and the range of lines it hides.</summary>
        public struct Range
        {
            public Range(int header, int first, int last)
            {
                Header = header;
                First = first;
                Last = last;
            }

            public int Header;

            public int First;

            public int Last;

            public int Count
            {
                get { return Last - First + 1; }
            }
        }
    }
}
