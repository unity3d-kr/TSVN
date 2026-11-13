using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using Task = System.Threading.Tasks.Task;

namespace SamirBoulema.TSVN.Helpers
{
    public static class FileHelper
    {
        private const string DEFAULT_PROC_PATH = @"C:\Program Files\TortoiseSVN\bin\TortoiseProc.exe";

        public static string GetTortoiseSvnProc()
        {
            var path = GetRegKeyValue();

            if (string.IsNullOrEmpty(path))
            {
                path = DEFAULT_PROC_PATH;
            }

            return path;
        }

        public static string GetSvnExec() 
            => GetTortoiseSvnProc().Replace("TortoiseProc.exe", "svn.exe");

        public static async Task OpenFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            await VS.Commands.ExecuteAsync("File.OpenFile", filePath);
        }

        /// <summary>
        /// Get the path of the file on which to act upon. 
        /// This can be different depending on where the TSVN context menu was used
        /// </summary>
        /// <returns>File path</returns>
        public static async Task<string> GetPath()
        {
            var windowFrame = await VS.Windows.GetCurrentWindowAsync();
            var solutionExplorerIsActive = windowFrame.Guid == new Guid(WindowGuids.SolutionExplorer);

            // Context menu in the Solution Explorer
            if (solutionExplorerIsActive)
            {
                var selectedItem = await VS.Solutions.GetActiveItemAsync();

                if (selectedItem != null)
                {
                    if (selectedItem.Type == SolutionItemType.Project ||
                        selectedItem.Type == SolutionItemType.Solution)
                    {
                        return Path.GetDirectoryName(selectedItem.FullPath);
                    }
                    else if (selectedItem.Type == SolutionItemType.PhysicalFile)
                    {
                        return selectedItem.FullPath;
                    }
                }
            }

            // Context menu in the Code Editor
            var documentView = await VS.Documents.GetActiveDocumentViewAsync();
            return documentView?.Document?.FilePath;
        }

        /// <summary>
        /// The file the active editor is showing, or null.
        ///
        /// A toolbar button is asked for its status on every UI update, and there can be a
        /// dozen of them, so this resolves the active document frame directly instead of
        /// going through the async helpers above: those reach for the solution selection and
        /// marshal to the UI thread, which is far too much to do inside QueryStatus.
        /// </summary>
        public static string GetActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var monitor = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(IVsMonitorSelection))
                as IVsMonitorSelection;

            if (monitor == null)
            {
                return null;
            }

            object frameValue;

            if (monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out frameValue) != VSConstants.S_OK)
            {
                return null;
            }

            var frame = frameValue as IVsWindowFrame;

            if (frame == null)
            {
                return null;
            }

            object documentValue;

            if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out documentValue) != VSConstants.S_OK)
            {
                return null;
            }

            var filePath = documentValue as string;

            return string.IsNullOrEmpty(filePath) ? null : filePath;
        }

        private static string GetRegKeyValue()
        {
            var localMachineKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);

            return localMachineKey
                .OpenSubKey(@"SOFTWARE\TortoiseSVN")
                ?.GetValue("ProcPath", DEFAULT_PROC_PATH)
                ?.ToString();
        }
    }
}
