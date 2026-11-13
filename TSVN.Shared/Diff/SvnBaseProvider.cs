using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SamirBoulema.TSVN.Helpers;
using Task = System.Threading.Tasks.Task;

namespace SamirBoulema.TSVN.Diff
{
    /// <summary>
    /// Reads the BASE (pristine) revision of one file out of the working copy with
    /// <c>svn cat -r BASE</c>.
    ///
    /// This is the only subprocess the diff markers ever start, and it runs once per file
    /// (cached), not once per keystroke. All the actual comparing happens in memory against
    /// the editor buffer, which keeps the markers correct for unsaved edits and keeps typing
    /// free of disk I/O.
    /// </summary>
    public static class SvnBaseProvider
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
        private const int CommandTimeoutMs = 15000;

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the BASE text, or null when the file is not versioned, is newly added,
        /// or svn could not be run.
        /// </summary>
        public static async Task<string> GetBaseTextAsync(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string key;

            try
            {
                key = Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return null;
            }

            CacheEntry cached;

            if (Cache.TryGetValue(key, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached.Text;
            }

            var text = await Task.Run(() => ReadBase(filePath), cancellationToken).ConfigureAwait(false);

            Cache[key] = new CacheEntry(text, DateTime.UtcNow + CacheLifetime);
            return text;
        }

        public static void Invalidate(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                CacheEntry ignored;
                Cache.TryRemove(Path.GetFullPath(filePath), out ignored);
            }
            catch (Exception)
            {
                // Nothing worth reporting: the cache simply stays valid a bit longer.
            }
        }

        private static string ReadBase(string filePath)
        {
            try
            {
                var svn = FileHelper.GetSvnExec();

                if (!File.Exists(svn))
                {
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = svn,
                    Arguments = "cat -r BASE -- " + Quote(filePath),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();

                    // Drain stderr concurrently: a large error message would otherwise fill
                    // the pipe and deadlock the stdout read. The pump ends with the process,
                    // so there is nothing to wait on afterwards and no result to collect.
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            process.StandardError.ReadToEnd();
                        }
                        catch (Exception)
                        {
                            // The pipe closes when the process dies; the message is not needed.
                        }
                    });

                    string text;

                    // Read the raw stream so the file's own encoding wins instead of the
                    // console code page. StreamReader still honours a byte order mark.
                    using (var reader = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false), true))
                    {
                        text = reader.ReadToEnd();
                    }

                    if (!process.WaitForExit(CommandTimeoutMs))
                    {
                        TryKill(process);
                        return null;
                    }

                    return process.ExitCode == 0 ? text : null;
                }
            }
            catch (Exception e)
            {
                LogHelper.Log("SvnBaseProvider.ReadBase", e);
                return null;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private sealed class CacheEntry
        {
            public CacheEntry(string text, DateTime expiresUtc)
            {
                Text = text;
                ExpiresUtc = expiresUtc;
            }

            public string Text { get; private set; }

            public DateTime ExpiresUtc { get; private set; }
        }
    }
}
