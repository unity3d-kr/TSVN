using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Process = System.Diagnostics.Process;
using SamirBoulema.TSVN.Options;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;
using Community.VisualStudio.Toolkit;
using Settings = SamirBoulema.TSVN.Properties.Settings;

namespace SamirBoulema.TSVN.Helpers
{
    public static class CommandHelper
    {
        public static async Task Commit()
            => await RunTortoiseSvnCommand("commit");

        public static async Task Commit(string filePath)
            => await RunTortoiseSvnFileCommand("commit", filePath: filePath);

        public static async Task Revert()
            => await RunTortoiseSvnCommand("revert");

        public static async Task Revert(string filePath)
            => await RunTortoiseSvnFileCommand("revert", filePath: filePath);

        public static async Task ShowDifferences(string filePath)
            => await RunTortoiseSvnFileCommand("diff", filePath: filePath);

        /// <summary>
        /// Runs <c>svn status</c> over <paramref name="repositoryRoot"/> and streams the entry
        /// count to <paramref name="progress"/> as it arrives.
        ///
        /// A large working copy takes a long time here, so the caller can cancel: the process
        /// tree is killed, which also stops the <c>cmd.exe</c> wrapper, and whatever was read so
        /// far is discarded by the caller.
        /// </summary>
        public static async Task<List<string>> GetPendingChanges(
            string repositoryRoot,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            var pendingChanges = new List<string>();

            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return pendingChanges;
            }

            Process proc = null;

            try
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c cd /D \"{repositoryRoot}\" && \"{FileHelper.GetSvnExec()}\" status" + (Settings.Default.HideUnversioned ? " -q" : string.Empty),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                proc.Start();

                // Drained in parallel: an unread stderr pipe blocks the child once it fills up.
                var errors = proc.StandardError.ReadToEndAsync();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await proc.StandardOutput.ReadLineAsync();

                    if (line == null)
                    {
                        break;
                    }

                    pendingChanges.Add(line);

                    if (progress != null && pendingChanges.Count % 25 == 0)
                    {
                        progress.Report(pendingChanges.Count);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    KillProcessTree(proc);
                    pendingChanges.Clear();
                }

                await errors;
                proc.WaitForExit(5000);
            }
            catch (Exception e)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    LogHelper.Log("GetPendingChanges", e);
                }
            }
            finally
            {
                if (proc != null)
                {
                    proc.Dispose();
                }
            }

            return pendingChanges;
        }

        /// <summary>
        /// Kills <paramref name="proc"/> together with its children. <c>svn.exe</c> runs under
        /// <c>cmd.exe</c>, so killing the wrapper alone would leave the working copy scan running.
        /// </summary>
        private static void KillProcessTree(Process proc)
        {
            try
            {
                if (proc.HasExited)
                {
                    return;
                }

                var kill = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/T /F /PID {proc.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                using (kill)
                {
                    kill.Start();
                    kill.WaitForExit(3000);
                }
            }
            catch (Exception e)
            {
                LogHelper.Log("KillProcessTree", e);
            }
        }

        public static async Task<string> GetRepositoryRoot(string path = "")
        {
            try
            {
                // Override any logic with the solution specific Root Folder setting
                var options = await OptionsHelper.GetOptions();

                if (!string.IsNullOrEmpty(options.RootFolder))
                {
                    return options.RootFolder;
                }

                // Try to find the current working folder, either by open document or by open solution
                if (string.IsNullOrEmpty(path))
                {
                    var solution = await VS.Solutions.GetCurrentSolutionAsync();
                    var documentView = await VS.Documents.GetActiveDocumentViewAsync();

                    if (!string.IsNullOrEmpty(solution?.FullPath))
                    {
                        path = Path.GetDirectoryName(solution.FullPath);
                    }
                    else if (!string.IsNullOrEmpty(documentView?.Document?.FilePath))
                    {
                        path = Path.GetDirectoryName(documentView.Document.FilePath);
                    }
                }

                // No solution or file open, we have no way of determining repository root.
                // Fail silently.
                if (string.IsNullOrEmpty(path))
                {
                    return string.Empty;
                }

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c cd /D \"{path}\" && \"{FileHelper.GetSvnExec()}\" info --show-item wc-root",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                proc.Start();

                var errors = string.Empty;

                while (!proc.StandardError.EndOfStream)
                {
                    errors += await proc.StandardError.ReadLineAsync();
                }

                while (!proc.StandardOutput.EndOfStream)
                {
                    options.RootFolder = await proc.StandardOutput.ReadLineAsync();
                }

                await OptionsHelper.SaveOptions(options);

                if (string.IsNullOrEmpty(options.RootFolder))
                {
                    await ShowMissingSolutionDirMessage();
                }

                return options.RootFolder;
            }
            catch (Exception e)
            {
                LogHelper.Log(e);
            }

            await ShowMissingSolutionDirMessage();

            return string.Empty;
        }

        private static async Task ShowMissingSolutionDirMessage()
        {
            await VS.MessageBox.ShowErrorAsync("Missing Working Copy Root Path",
                "Unable to determine the solution directory location. Please manually set the directory location in the settings.");
        }

        public static async Task StartProcess(string application, string args)
        {
            try
            {
                Process.Start(application, args);
            }
            catch (Exception)
            {
                await VS.MessageBox.ShowErrorAsync("TortoiseSVN not found", "TortoiseSVN not found. Did you install TortoiseSVN?");
            }
        }

        public static async Task RunTortoiseSvnCommand(string command, string args = "")
        {
            var solutionDir = await GetRepositoryRoot();

            if (string.IsNullOrEmpty(solutionDir))
            {
                return;
            }

            var tortoiseProc = FileHelper.GetTortoiseSvnProc();

            var options = await OptionsHelper.GetOptions();
            var closeOnEnd = options.CloseOnEnd ? 1 : 0;

            await StartProcess(tortoiseProc, $"/command:{command} /path:\"{solutionDir}\" {args} /closeonend:{closeOnEnd}");
        }

        public static async Task RunTortoiseSvnFileCommand(string command, string args = "", string filePath = "")
        {
            if (string.IsNullOrEmpty(filePath))
            {
                filePath = await FileHelper.GetPath();
            }

            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            var tortoiseProc = FileHelper.GetTortoiseSvnProc();

            var options = await OptionsHelper.GetOptions();
            var closeOnEnd = options.CloseOnEnd ? 1 : 0;

            await StartProcess (tortoiseProc, $"/command:{command} /path:\"{filePath}\" {args} /closeonend:{closeOnEnd}");
        }
    }
}
