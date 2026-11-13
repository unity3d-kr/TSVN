using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// Shares the computed chunk list between the gutter margin and the line classifier.
    /// Both are MEF components on the same text buffer, so neither can own the other.
    /// </summary>
    public static class DiffChunkCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<ITextBuffer, IReadOnlyList<DiffChunk>> Entries =
            new Dictionary<ITextBuffer, IReadOnlyList<DiffChunk>>();

        private static readonly IReadOnlyList<DiffChunk> Empty = new List<DiffChunk>();

        /// <summary>Raised, on the thread that published the chunks, whenever a buffer changed.</summary>
        public static event EventHandler<ITextBuffer> Changed;

        public static void Set(ITextBuffer buffer, IReadOnlyList<DiffChunk> chunks)
        {
            if (buffer == null)
            {
                return;
            }

            lock (Gate)
            {
                Entries[buffer] = chunks == null || chunks.Count == 0 ? Empty : chunks;
            }

            RaiseChanged(buffer);
        }

        public static IReadOnlyList<DiffChunk> Get(ITextBuffer buffer)
        {
            if (buffer == null)
            {
                return Empty;
            }

            lock (Gate)
            {
                IReadOnlyList<DiffChunk> chunks;

                if (Entries.TryGetValue(buffer, out chunks))
                {
                    return chunks;
                }
            }

            return Empty;
        }

        public static void Clear(ITextBuffer buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (Gate)
            {
                Entries.Remove(buffer);
            }

            RaiseChanged(buffer);
        }

        private static void RaiseChanged(ITextBuffer buffer)
        {
            var handler = Changed;

            if (handler != null)
            {
                handler(null, buffer);
            }
        }
    }
}
