using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;

namespace SamirBoulema.TSVN.Options
{
    /// <summary>Shape of %LOCALAPPDATA%\TSVN\toolbar.json.</summary>
    public sealed class ToolbarSettings
    {
        public bool ShowFileButtons = false;
    }

    /// <summary>
    /// The switch that puts the file scoped buttons on the TortoiseSVN toolbar, and the UI
    /// context that makes switching them work.
    ///
    /// A button placed on a top level toolbar cannot be hidden by its own QueryStatus:
    /// Visual Studio disables such a button instead of removing it, which is why the command
    /// table used to carry only the commands that act on a working copy. The mechanism the
    /// shell does honour is a visibility constraint, evaluated while the toolbar is built. So
    /// every file button carries a constraint on a UI context of our own, and this class turns
    /// that context on or off. Off is the default: the toolbar belongs to the working copy,
    /// and putting a dozen file commands on it is a choice, not a surprise.
    /// </summary>
    public static class ToolbarState
    {
        /// <summary>Matches the guidTSVNFileToolbarContext symbol in TSVN.vsct.</summary>
        public static readonly Guid FileButtonsContext = new Guid("b3e7c1d4-9a2f-4f68-8c05-7d1e6a92b4f3");

        private static readonly object Gate = new object();

        private static bool _loaded;
        private static bool _showFileButtons;

        /// <summary>Raised after the switch changed.</summary>
        public static event EventHandler Changed;

        public static string StoragePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TSVN",
                    "toolbar.json");
            }
        }

        public static bool ShowFileButtons
        {
            get
            {
                EnsureLoaded();

                lock (Gate)
                {
                    return _showFileButtons;
                }
            }

            set
            {
                EnsureLoaded();

                lock (Gate)
                {
                    if (_showFileButtons == value)
                    {
                        return;
                    }

                    _showFileButtons = value;
                }

                ThreadHelper.ThrowIfNotOnUIThread();

                Save();
                Apply();

                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Pushes the stored switch into the shell. Called once when the package loads, so a
        /// toolbar that was customised in an earlier session comes back the same way.
        /// </summary>
        public static void Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var monitor = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsShellMonitorSelection))
                as IVsMonitorSelection;

            if (monitor == null)
            {
                return;
            }

            // A custom context is addressed by a cookie, not by its guid. Raising it makes
            // the shell re-evaluate every visibility constraint that names it, which is what
            // puts the buttons on the toolbar or takes them back off.
            var context = FileButtonsContext;
            uint cookie;

            if (monitor.GetCmdUIContextCookie(ref context, out cookie) != Microsoft.VisualStudio.VSConstants.S_OK)
            {
                return;
            }

            monitor.SetCmdUIContext(cookie, ShowFileButtons ? 1 : 0);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (Gate)
            {
                if (_loaded)
                {
                    return;
                }

                _loaded = true;

                try
                {
                    var path = StoragePath;

                    if (!File.Exists(path))
                    {
                        return;
                    }

                    var settings = JsonConvert.DeserializeObject<ToolbarSettings>(File.ReadAllText(path));

                    if (settings != null)
                    {
                        _showFileButtons = settings.ShowFileButtons;
                    }
                }
                catch (Exception)
                {
                    // A damaged file means the default, which is off, is used.
                }
            }
        }

        private static void Save()
        {
            try
            {
                var settings = new ToolbarSettings { ShowFileButtons = _showFileButtons };
                var path = StoragePath;
                var folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception)
            {
                // Losing the setting is not worth failing the command over.
            }
        }
    }
}
