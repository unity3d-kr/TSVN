using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>Shape of %LOCALAPPDATA%\TSVN\diff-markers.json.</summary>
    public sealed class DiffMarkersSettings
    {
        public bool IgnoreWhitespace = false;

        public bool ShowOverviewBar = true;

        public List<string> Files = new List<string>();

        public DiffMarkersSettings Clone()
        {
            return new DiffMarkersSettings
            {
                IgnoreWhitespace = IgnoreWhitespace,
                ShowOverviewBar = ShowOverviewBar,
                Files = new List<string>(Files ?? new List<string>())
            };
        }
    }

    /// <summary>
    /// Per-file opt-in for the diff markers, plus the two global switches.
    ///
    /// The markers need a BASE revision of the file, which means reading the pristine copy
    /// out of the working copy the first time a document is opened. That is cheap but not
    /// free, so the feature is off by default and switched on for one file at a time from
    /// the TortoiseSVN menu. The selection is user scoped and survives restarts.
    /// </summary>
    public static class DiffMarkersState
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static bool _ignoreWhitespace;
        private static bool _showOverviewBar = true;

        /// <summary>Raised after the enabled set or a global switch changed.</summary>
        public static event EventHandler Changed;

        public static string StoragePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TSVN",
                    "diff-markers.json");
            }
        }

        public static bool IgnoreWhitespace
        {
            get
            {
                EnsureLoaded();

                lock (Gate)
                {
                    return _ignoreWhitespace;
                }
            }

            set
            {
                EnsureLoaded();

                lock (Gate)
                {
                    if (_ignoreWhitespace == value)
                    {
                        return;
                    }

                    _ignoreWhitespace = value;
                }

                Save();
                RaiseChanged();
            }
        }

        public static bool ShowOverviewBar
        {
            get
            {
                EnsureLoaded();

                lock (Gate)
                {
                    return _showOverviewBar;
                }
            }

            set
            {
                EnsureLoaded();

                lock (Gate)
                {
                    if (_showOverviewBar == value)
                    {
                        return;
                    }

                    _showOverviewBar = value;
                }

                Save();
                RaiseChanged();
            }
        }

        public static bool IsEnabled(string filePath)
        {
            var normalized = Normalize(filePath);

            if (normalized == null)
            {
                return false;
            }

            EnsureLoaded();

            lock (Gate)
            {
                return Files.Contains(normalized);
            }
        }

        /// <summary>Adds or removes <paramref name="filePath"/> and returns the new state.</summary>
        public static bool SetEnabled(string filePath, bool enabled)
        {
            var normalized = Normalize(filePath);

            if (normalized == null)
            {
                return false;
            }

            EnsureLoaded();

            lock (Gate)
            {
                if (enabled)
                {
                    Files.Add(normalized);
                }
                else
                {
                    Files.Remove(normalized);
                }
            }

            Save();
            RaiseChanged();

            return enabled;
        }

        public static bool Toggle(string filePath)
        {
            return SetEnabled(filePath, !IsEnabled(filePath));
        }

        private static string Normalize(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return null;
            }
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

                    var settings = JsonConvert.DeserializeObject<DiffMarkersSettings>(File.ReadAllText(path));

                    if (settings == null)
                    {
                        return;
                    }

                    _ignoreWhitespace = settings.IgnoreWhitespace;
                    _showOverviewBar = settings.ShowOverviewBar;

                    if (settings.Files == null)
                    {
                        return;
                    }

                    foreach (var entry in settings.Files)
                    {
                        var normalized = Normalize(entry);

                        if (normalized != null)
                        {
                            Files.Add(normalized);
                        }
                    }
                }
                catch (Exception e)
                {
                    Helpers.LogHelper.Log("DiffMarkersState.Load", e);
                }
            }
        }

        private static void Save()
        {
            try
            {
                var settings = new DiffMarkersSettings();

                lock (Gate)
                {
                    settings.IgnoreWhitespace = _ignoreWhitespace;
                    settings.ShowOverviewBar = _showOverviewBar;
                    settings.Files.AddRange(Files);
                }

                settings.Files.Sort(StringComparer.OrdinalIgnoreCase);

                var path = StoragePath;
                var folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception e)
            {
                Helpers.LogHelper.Log("DiffMarkersState.Save", e);
            }
        }

        private static void RaiseChanged()
        {
            var handler = Changed;

            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
    }
}
