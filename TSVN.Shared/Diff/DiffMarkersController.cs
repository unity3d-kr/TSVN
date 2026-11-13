using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SamirBoulema.TSVN.Helpers;
using Task = System.Threading.Tasks.Task;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// Owns the diff state for one text view: reads BASE once, recomputes on a debounce and
    /// publishes the result into <see cref="DiffChunkCache"/> for the margins and classifier.
    ///
    /// Recomputation happens 500 ms after the last keystroke, off the UI thread, against the
    /// in-memory buffer. Nothing touches the disk while typing, and a file whose markers are
    /// switched off never starts a subprocess at all.
    /// </summary>
    internal sealed class DiffMarkersController : IDisposable
    {
        private const int DebounceMilliseconds = 500;

        private readonly IWpfTextView _textView;
        private readonly ITextBuffer _buffer;
        private readonly string _filePath;
        private readonly DispatcherTimer _debounce;

        private int _generation;
        private bool _forceRefresh;
        private bool _disposed;

        public DiffMarkersController(IWpfTextView textView, string filePath)
        {
            _textView = textView;
            _buffer = textView.TextBuffer;
            _filePath = filePath;

            _debounce = new DispatcherTimer(DispatcherPriority.Background);
            _debounce.Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds);
            _debounce.Tick += OnDebounceTick;

            _buffer.Changed += OnBufferChanged;
            DiffMarkersState.Changed += OnStateChanged;
        }

        /// <summary>Raised on the UI thread whenever <see cref="Chunks"/> was replaced.</summary>
        public event EventHandler<EventArgs> ChunksChanged;

        public string FilePath
        {
            get { return _filePath; }
        }

        public IReadOnlyList<DiffChunk> Chunks { get; private set; }

        public void Start()
        {
            Schedule(true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _buffer.Changed -= OnBufferChanged;
            DiffMarkersState.Changed -= OnStateChanged;

            _debounce.Stop();
            _debounce.Tick -= OnDebounceTick;

            DiffChunkCache.Clear(_buffer);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            Schedule(false);
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            // The toggle itself asks for a forced refresh because the BASE copy may have
            // changed underneath us since the last read.
            Schedule(true);
        }

        private void Schedule(bool force)
        {
            if (_disposed)
            {
                return;
            }

            if (force)
            {
                _forceRefresh = true;
            }

            _debounce.Stop();
            _debounce.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();

            var force = _forceRefresh;
            _forceRefresh = false;

            RecomputeAsync(force).FireAndForget();
        }

        private async Task RecomputeAsync(bool force)
        {
            if (_disposed)
            {
                return;
            }

            var generation = ++_generation;
            var filePath = _filePath;

            if (!DiffMarkersState.IsEnabled(filePath))
            {
                Chunks = null;
                DiffChunkCache.Clear(_buffer);
                RaiseChunksChanged();
                return;
            }

            if (force)
            {
                SvnBaseProvider.Invalidate(filePath);
            }

            // Snapshot the buffer on the UI thread; everything after this point is off-thread.
            var bufferText = _buffer.CurrentSnapshot.GetText();
            var ignoreWhitespace = DiffMarkersState.IgnoreWhitespace;

            string baseText = null;

            try
            {
                baseText = await SvnBaseProvider.GetBaseTextAsync(filePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LogHelper.Log("DiffMarkersController.GetBase", e);
            }

            if (_disposed || generation != _generation)
            {
                return;
            }

            IReadOnlyList<DiffChunk> chunks;

            try
            {
                chunks = await Task.Run(() => ComputeChunks(baseText, bufferText, ignoreWhitespace)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LogHelper.Log("DiffMarkersController.Compute", e);
                return;
            }

            if (_disposed || generation != _generation)
            {
                return;
            }

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            catch (Exception)
            {
                return;
            }

            if (_disposed || generation != _generation)
            {
                return;
            }

            Chunks = chunks;
            DiffChunkCache.Set(_buffer, chunks);
            RaiseChunksChanged();
        }

        private static IReadOnlyList<DiffChunk> ComputeChunks(string baseText, string bufferText, bool ignoreWhitespace)
        {
            var newLines = LineDiff.SplitLines(bufferText);

            if (baseText == null)
            {
                // No BASE revision: either the file is not versioned yet or it was just added,
                // in which case every line is new.
                IReadOnlyList<DiffChunk> added = newLines.Length == 0
                    ? new List<DiffChunk>()
                    : new List<DiffChunk> { new DiffChunk(DiffKind.Added, 1, newLines.Length, 1, 0) };

                return added;
            }

            var oldLines = LineDiff.SplitLines(baseText);
            return LineDiff.Compute(oldLines, newLines, ignoreWhitespace);
        }

        private void RaiseChunksChanged()
        {
            var handler = ChunksChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
