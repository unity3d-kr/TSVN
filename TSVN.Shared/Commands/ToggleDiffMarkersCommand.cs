using System;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using SamirBoulema.TSVN.Diff;
using Task = System.Threading.Tasks.Task;

namespace SamirBoulema.TSVN.Commands
{
    /// <summary>
    /// Turns the diff markers on or off for the file that is currently open.
    ///
    /// The markers have to read the BASE revision of the file, so they are deliberately not
    /// always on: a code file only pays for them once it is switched on from the
    /// TortoiseSVN menu. The choice is remembered per file.
    /// </summary>
    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toggleDiffMarkersCommand)]
    internal sealed class ToggleDiffMarkersCommand : BaseCommand<ToggleDiffMarkersCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var filePath = await GetActiveFilePathAsync();

            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            if (!DiffMarkersState.Toggle(filePath))
            {
                return;
            }

            var baseText = await SvnBaseProvider.GetBaseTextAsync(filePath, CancellationToken.None);

            if (baseText == null)
            {
                await VS.MessageBox.ShowWarningAsync(
                    "Diff Markers",
                    "This file has no BASE revision yet, so the whole file is shown as new. " +
                    "Add it to source control and commit it to get a line by line comparison.");
            }
        }

        protected override void BeforeQueryStatus(EventArgs e)
        {
            var filePath = ThreadHelper.JoinableTaskFactory.Run(GetActiveFilePathAsync);
            var hasFile = !string.IsNullOrEmpty(filePath);

            // Visual Studio takes the caption from the command table resource and ignores
            // MenuCommand.Text set here, so the label stays "Show Diff Markers" and the check
            // mark is what reports whether the markers are currently on.
            Command.Enabled = hasFile;
            Command.Checked = hasFile && DiffMarkersState.IsEnabled(filePath);
        }

        private static async System.Threading.Tasks.Task<string> GetActiveFilePathAsync()
        {
            var view = await VS.Documents.GetActiveDocumentViewAsync();

            return view == null || view.Document == null ? null : view.Document.FilePath;
        }
    }
}
