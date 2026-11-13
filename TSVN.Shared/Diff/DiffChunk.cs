using System.Collections.Generic;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>The kind of change a gutter marker represents.</summary>
    public enum DiffKind
    {
        /// <summary>The line exists in BASE but its content changed.</summary>
        Modified,

        /// <summary>The line does not exist in BASE at all.</summary>
        Added,

        /// <summary>One or more BASE lines were removed at this position.</summary>
        Deleted
    }

    /// <summary>
    /// One contiguous run of changed lines, expressed against the text buffer that is
    /// currently shown in the editor. All line numbers are 1-based.
    /// </summary>
    public sealed class DiffChunk
    {
        public DiffChunk(DiffKind kind, int startLine, int lineCount, int oldStartLine, int oldLineCount)
        {
            Kind = kind;
            StartLine = startLine;
            LineCount = lineCount;
            OldStartLine = oldStartLine;
            OldLineCount = oldLineCount;
        }

        public DiffKind Kind { get; private set; }

        /// <summary>First buffer line covered by the marker. Always at least 1.</summary>
        public int StartLine { get; private set; }

        /// <summary>
        /// Number of buffer lines covered. Always at least 1, because a pure deletion still
        /// needs a marker: it is anchored to the line that follows the removed block.
        /// </summary>
        public int LineCount { get; private set; }

        /// <summary>First BASE line of the corresponding run.</summary>
        public int OldStartLine { get; private set; }

        /// <summary>Number of BASE lines in the corresponding run. Zero for insertions.</summary>
        public int OldLineCount { get; private set; }

        public int EndLine
        {
            get { return StartLine + LineCount - 1; }
        }

        public override string ToString()
        {
            return string.Format("{0} new {1}+{2} old {3}+{4}", Kind, StartLine, LineCount, OldStartLine, OldLineCount);
        }
    }

    /// <summary>Convenience helpers over a chunk list.</summary>
    public static class DiffChunkExtensions
    {
        /// <summary>Returns the chunk covering <paramref name="lineNumber"/>, or null.</summary>
        public static DiffChunk ChunkAt(this IReadOnlyList<DiffChunk> chunks, int lineNumber)
        {
            if (chunks == null)
            {
                return null;
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                if (lineNumber >= chunk.StartLine && lineNumber <= chunk.EndLine)
                {
                    return chunk;
                }
            }

            return null;
        }
    }
}
