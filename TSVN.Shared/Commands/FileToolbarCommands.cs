using System;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using SamirBoulema.TSVN.Diff;
using SamirBoulema.TSVN.Helpers;
using SamirBoulema.TSVN.Options;
using Task = System.Threading.Tasks.Task;

namespace SamirBoulema.TSVN.Commands
{
    /// <summary>
    /// A file scoped command, shown as a button on the TortoiseSVN toolbar.
    ///
    /// These are separate command ids from the ones in the file context menu, not another
    /// placement of them. Visibility belongs to the command and applies to every placement,
    /// so reusing the context menu ids would mean hiding those entries as well whenever the
    /// toolbar switch is off. Each class below therefore carries its own id and forwards to
    /// the same TortoiseProc verb, which keeps the two surfaces independent.
    ///
    /// The button acts on the file the editor is showing. The context menu falls back to the
    /// Solution Explorer selection because that is where it was invoked from; a toolbar has
    /// no such origin, and a stale selection there would act on a file the user is not
    /// looking at.
    /// </summary>
    internal abstract class FileToolbarCommand<T> : BaseCommand<T> where T : FileToolbarCommand<T>, new()
    {
        /// <summary>The TortoiseProc verb, the same one the file context menu uses.</summary>
        protected abstract string Verb { get; }

        protected override Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            return CommandHelper.RunTortoiseSvnFileCommand(Verb);
        }

        protected override void BeforeQueryStatus(EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The visibility constraint in TSVN.vsct keeps the button out of the toolbar
            // while the switch is off; this keeps the two in step if the constraint has not
            // been re-evaluated yet.
            Command.Visible = ToolbarState.ShowFileButtons;
            Command.Enabled = !string.IsNullOrEmpty(FileHelper.GetActiveDocumentPath());
        }
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarShowLogCommand)]
    internal sealed class ToolbarShowLogCommand : FileToolbarCommand<ToolbarShowLogCommand>
    {
        protected override string Verb => "log";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarBlameCommand)]
    internal sealed class ToolbarBlameCommand : FileToolbarCommand<ToolbarBlameCommand>
    {
        protected override string Verb => "blame";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarDifferencesCommand)]
    internal sealed class ToolbarDifferencesCommand : FileToolbarCommand<ToolbarDifferencesCommand>
    {
        protected override string Verb => "diff";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarDiffPreviousCommand)]
    internal sealed class ToolbarDiffPreviousCommand : FileToolbarCommand<ToolbarDiffPreviousCommand>
    {
        protected override string Verb => "prevdiff";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarPropertiesCommand)]
    internal sealed class ToolbarPropertiesCommand : FileToolbarCommand<ToolbarPropertiesCommand>
    {
        protected override string Verb => "properties";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarUpdateCommand)]
    internal sealed class ToolbarUpdateCommand : FileToolbarCommand<ToolbarUpdateCommand>
    {
        protected override string Verb => "update";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarCommitCommand)]
    internal sealed class ToolbarCommitCommand : FileToolbarCommand<ToolbarCommitCommand>
    {
        protected override string Verb => "commit";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarRevertCommand)]
    internal sealed class ToolbarRevertCommand : FileToolbarCommand<ToolbarRevertCommand>
    {
        protected override string Verb => "revert";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarAddCommand)]
    internal sealed class ToolbarAddCommand : FileToolbarCommand<ToolbarAddCommand>
    {
        protected override string Verb => "add";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarDeleteCommand)]
    internal sealed class ToolbarDeleteCommand : FileToolbarCommand<ToolbarDeleteCommand>
    {
        protected override string Verb => "remove";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarRenameCommand)]
    internal sealed class ToolbarRenameCommand : FileToolbarCommand<ToolbarRenameCommand>
    {
        protected override string Verb => "rename";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarLockCommand)]
    internal sealed class ToolbarLockCommand : FileToolbarCommand<ToolbarLockCommand>
    {
        protected override string Verb => "lock";
    }

    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarUnlockCommand)]
    internal sealed class ToolbarUnlockCommand : FileToolbarCommand<ToolbarUnlockCommand>
    {
        protected override string Verb => "unlock";
    }

    /// <summary>
    /// The diff marker switch, mirrored on the toolbar.
    ///
    /// It is not a TortoiseProc command, so it gets its own handler, and it reports whether
    /// the markers are on for the file in front of the user with a check mark.
    /// </summary>
    [Command(PackageGuids.guidTSVNCmdSetString, PackageIds.toolbarDiffMarkersCommand)]
    internal sealed class ToolbarDiffMarkersCommand : BaseCommand<ToolbarDiffMarkersCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var filePath = FileHelper.GetActiveDocumentPath();

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
            ThreadHelper.ThrowIfNotOnUIThread();

            var filePath = FileHelper.GetActiveDocumentPath();
            var hasFile = !string.IsNullOrEmpty(filePath);

            Command.Visible = ToolbarState.ShowFileButtons;
            Command.Enabled = hasFile;
            Command.Checked = hasFile && DiffMarkersState.IsEnabled(filePath);
        }
    }
}
