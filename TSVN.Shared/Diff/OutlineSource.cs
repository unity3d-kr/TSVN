using System;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// The outlining manager of a view, resolved when it is needed rather than when the margin is
    /// built.
    ///
    /// A margin provider is asked for its margin while the view is still being created, and the
    /// editor has no outlining manager at that point: the language service installs the regions
    /// later. Asking once in the factory therefore yields null for the whole life of the view, and
    /// every consumer then silently behaves as if nothing were folded. Resolving lazily, and
    /// notifying once a manager appears, keeps the fold information flowing without the margins
    /// having to know about the ordering.
    /// </summary>
    public sealed class OutlineSource
    {
        private readonly IWpfTextView _textView;
        private readonly IOutliningManagerService _service;
        private readonly DispatcherTimer _defer;

        private IOutliningManager _manager;
        private bool _hooked;

        public OutlineSource(IWpfTextView textView, IOutliningManagerService service)
        {
            _textView = textView;
            _service = service;

            // Deferred, because the notification is triggered from inside a render: re-rendering
            // synchronously there would paint twice with half updated state, and the outer pass
            // would then overwrite the inner one with fold-blind marks.
            _defer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1)
            };

            _defer.Tick += OnDeferredTick;
        }

        /// <summary>Raised when the folded regions change, or when a manager first becomes available.</summary>
        public event EventHandler Changed;

        /// <summary>Current manager, or null when this view has no outlining at all.</summary>
        public IOutliningManager Manager
        {
            get
            {
                if (_manager == null)
                {
                    _manager = Resolve();

                    if (_manager != null)
                    {
                        Hook();

                        // The caller asked because it was about to paint, and until now it had no
                        // fold information. One extra pass closes that gap.
                        Notify();
                    }
                }

                return _manager;
            }
        }

        public void Dispose()
        {
            _defer.Stop();
            _defer.Tick -= OnDeferredTick;

            if (_manager != null && _hooked)
            {
                _manager.RegionsChanged -= OnRegionsChanged;
                _hooked = false;
            }

            _manager = null;
        }

        private IOutliningManager Resolve()
        {
            if (_service != null)
            {
                try
                {
                    var manager = _service.GetOutliningManager(_textView);

                    if (manager != null)
                    {
                        return manager;
                    }
                }
                catch (Exception)
                {
                    // Outlining can be unavailable while the view is being torn down.
                }
            }

            // Some hosts put the manager on the view itself instead of handing out a service.
            try
            {
                IOutliningManager manager;

                if (_textView.Properties.TryGetProperty(typeof(IOutliningManager), out manager))
                {
                    return manager;
                }
            }
            catch (Exception)
            {
                // Property bags differ between hosts; a miss only costs accuracy.
            }

            return null;
        }

        private void Hook()
        {
            if (_hooked)
            {
                return;
            }

            _manager.RegionsChanged += OnRegionsChanged;
            _hooked = true;
        }

        private void OnRegionsChanged(object sender, RegionsChangedEventArgs e)
        {
            Notify();
        }

        private void Notify()
        {
            if (!_defer.IsEnabled)
            {
                _defer.Start();
            }
        }

        private void OnDeferredTick(object sender, EventArgs e)
        {
            _defer.Stop();

            var handler = Changed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
