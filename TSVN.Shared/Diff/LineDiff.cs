using System;
using System.Collections.Generic;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// Line based diff between the BASE revision and the in-memory editor buffer.
    ///
    /// Only the changed regions are needed, so the algorithm trims the common prefix and
    /// suffix first and then runs a plain LCS over whatever is left. For typical edits that
    /// middle region is a few dozen lines. A hard cell budget keeps pathological files from
    /// allocating a huge table: past the budget the whole middle is reported as one modified
    /// block, which is still correct, just coarser.
    /// </summary>
    public static class LineDiff
    {
        /// <summary>Maximum number of LCS cells (old * new) before falling back to one block.</summary>
        private const long MaxCells = 2000000;

        private const int Equal = 0;
        private const int Insert = 1;
        private const int Delete = 2;

        public static IReadOnlyList<DiffChunk> Compute(
            IReadOnlyList<string> oldLines,
            IReadOnlyList<string> newLines,
            bool ignoreWhitespace)
        {
            var result = new List<DiffChunk>();

            if (oldLines == null || newLines == null)
            {
                return result;
            }

            var o = Normalize(oldLines, ignoreWhitespace);
            var n = Normalize(newLines, ignoreWhitespace);

            var prefix = 0;
            var maxPrefix = Math.Min(o.Length, n.Length);

            while (prefix < maxPrefix && string.Equals(o[prefix], n[prefix], StringComparison.Ordinal))
            {
                prefix++;
            }

            var suffix = 0;
            var maxSuffix = Math.Min(o.Length, n.Length) - prefix;

            while (suffix < maxSuffix &&
                   string.Equals(o[o.Length - 1 - suffix], n[n.Length - 1 - suffix], StringComparison.Ordinal))
            {
                suffix++;
            }

            var oldCount = o.Length - prefix - suffix;
            var newCount = n.Length - prefix - suffix;

            if (oldCount == 0 && newCount == 0)
            {
                return result;
            }

            if (oldCount == 0)
            {
                result.Add(new DiffChunk(DiffKind.Added, prefix + 1, newCount, prefix + 1, 0));
                return result;
            }

            if (newCount == 0)
            {
                result.Add(new DiffChunk(DiffKind.Deleted, AnchorForDeletion(prefix, n.Length), 1, prefix + 1, oldCount));
                return result;
            }

            var cells = (long)(oldCount + 1) * (newCount + 1);

            if (cells > MaxCells)
            {
                result.Add(new DiffChunk(DiffKind.Modified, prefix + 1, newCount, prefix + 1, oldCount));
                return result;
            }

            Walk(o, n, prefix, oldCount, newCount, result);
            return result;
        }

        /// <summary>A pure deletion is drawn on the line that follows the removed block.</summary>
        private static int AnchorForDeletion(int prefix, int newTotal)
        {
            var anchor = prefix + 1;

            if (anchor > newTotal)
            {
                anchor = Math.Max(1, newTotal);
            }

            return anchor;
        }

        private static string[] Normalize(IReadOnlyList<string> lines, bool ignoreWhitespace)
        {
            var copy = new string[lines.Count];

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? string.Empty;
                copy[i] = ignoreWhitespace ? line.Trim() : line;
            }

            return copy;
        }

        private static void Walk(
            string[] oldLines,
            string[] newLines,
            int prefix,
            int oldCount,
            int newCount,
            List<DiffChunk> result)
        {
            var width = newCount + 1;
            var table = new int[(oldCount + 1) * width];

            for (var i = 1; i <= oldCount; i++)
            {
                for (var j = 1; j <= newCount; j++)
                {
                    if (string.Equals(oldLines[prefix + i - 1], newLines[prefix + j - 1], StringComparison.Ordinal))
                    {
                        table[i * width + j] = table[(i - 1) * width + j - 1] + 1;
                    }
                    else
                    {
                        var up = table[(i - 1) * width + j];
                        var left = table[i * width + j - 1];
                        table[i * width + j] = up >= left ? up : left;
                    }
                }
            }

            // Backtrack to a forward-ordered edit script over the trimmed region.
            var script = new List<int>(oldCount + newCount);
            var x = oldCount;
            var y = newCount;

            while (x > 0 && y > 0)
            {
                if (string.Equals(oldLines[prefix + x - 1], newLines[prefix + y - 1], StringComparison.Ordinal))
                {
                    script.Add(Equal);
                    x--;
                    y--;
                }
                else if (table[(x - 1) * width + y] >= table[x * width + y - 1])
                {
                    script.Add(Delete);
                    x--;
                }
                else
                {
                    script.Add(Insert);
                    y--;
                }
            }

            while (x > 0)
            {
                script.Add(Delete);
                x--;
            }

            while (y > 0)
            {
                script.Add(Insert);
                y--;
            }

            script.Reverse();

            // Collapse runs of non-equal edits into chunks.
            var index = 0;
            var oldPosition = 0;
            var newPosition = 0;

            while (index < script.Count)
            {
                if (script[index] == Equal)
                {
                    index++;
                    oldPosition++;
                    newPosition++;
                    continue;
                }

                var runOldStart = oldPosition;
                var runNewStart = newPosition;
                var inserted = 0;
                var deleted = 0;

                while (index < script.Count && script[index] != Equal)
                {
                    if (script[index] == Insert)
                    {
                        inserted++;
                        newPosition++;
                    }
                    else
                    {
                        deleted++;
                        oldPosition++;
                    }

                    index++;
                }

                AddRun(result, prefix, runOldStart, runNewStart, inserted, deleted, newLines.Length);
            }
        }

        private static void AddRun(
            List<DiffChunk> result,
            int prefix,
            int runOldStart,
            int runNewStart,
            int inserted,
            int deleted,
            int newTotal)
        {
            if (inserted > 0 && deleted > 0)
            {
                result.Add(new DiffChunk(DiffKind.Modified, prefix + runNewStart + 1, inserted, prefix + runOldStart + 1, deleted));
            }
            else if (inserted > 0)
            {
                result.Add(new DiffChunk(DiffKind.Added, prefix + runNewStart + 1, inserted, prefix + runOldStart + 1, 0));
            }
            else if (deleted > 0)
            {
                var anchor = AnchorForDeletion(prefix + runNewStart, newTotal);
                result.Add(new DiffChunk(DiffKind.Deleted, anchor, 1, prefix + runOldStart + 1, deleted));
            }
        }

        /// <summary>Splits text into lines without the trailing line terminator.</summary>
        public static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new string[0];
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var count = lines.Length;

            // A trailing newline produces one empty entry that is not a real line.
            if (count > 0 && lines[count - 1].Length == 0)
            {
                count--;
            }

            if (count == lines.Length)
            {
                return lines;
            }

            var trimmed = new string[count];
            Array.Copy(lines, trimmed, count);
            return trimmed;
        }
    }
}
