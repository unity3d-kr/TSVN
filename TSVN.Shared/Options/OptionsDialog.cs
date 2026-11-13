using Community.VisualStudio.Toolkit;
using SamirBoulema.TSVN.Diff;
using SamirBoulema.TSVN.Helpers;
using System;
using System.IO;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;
using File = System.IO.File;
using Microsoft.VisualStudio.Shell;

namespace SamirBoulema.TSVN.Options
{
    public partial class OptionsDialog : Form
    {
        private Options options;

        public OptionsDialog()
        {
            InitializeComponent();

            // The diff marker switches are user scoped and apply immediately: the dialog's OK
            // button is disabled while no solution is open, and they do not belong to the
            // solution specific options below.
            ignoreWhitespaceCheckBox.CheckedChanged += (sender, args) =>
                DiffMarkersState.IgnoreWhitespace = ignoreWhitespaceCheckBox.Checked;

            showOverviewBarCheckBox.CheckedChanged += (sender, args) =>
                DiffMarkersState.ShowOverviewBar = showOverviewBarCheckBox.Checked;

            // Applied as it is ticked: the toolbar is rebuilt from the command table when the
            // UI context behind these buttons changes, so there is nothing to wait for.
            showFileToolbarCheckBox.CheckedChanged += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                ToolbarState.ShowFileButtons = showFileToolbarCheckBox.Checked;
            };

            Load += new EventHandler(OptionsDialog_Load);
        }

        private void OptionsDialog_Load(object sender, EventArgs e)
        {
            ignoreWhitespaceCheckBox.Checked = DiffMarkersState.IgnoreWhitespace;
            showOverviewBarCheckBox.Checked = DiffMarkersState.ShowOverviewBar;
            showFileToolbarCheckBox.Checked = ToolbarState.ShowFileButtons;

            LoadDialog().FireAndForget();
        }

        private async Task LoadDialog()
        {
            var solution = await VS.Solutions.GetCurrentSolutionAsync();
            var solutionFilePath = solution?.FullPath;

            if (File.Exists(solutionFilePath))
            {
                options = await OptionsHelper.GetOptions();
                rootFolderTextBox.Text = options.RootFolder;
                onItemAddedAddToSVNCheckBox.Checked = options.OnItemAddedAddToSVN;
                onItemRenamedRenameInSVNCheckBox.Checked = options.OnItemRenamedRenameInSVN;
                onItemRemovedRemoveFromSVNCheckBox.Checked = options.OnItemRemovedRemoveFromSVN;
                closeOnEndCheckBox.Checked = options.CloseOnEnd;
            }
            else
            {
                rootFolderTextBox.Enabled = false;
                onItemAddedAddToSVNCheckBox.Checked = false;
                onItemRenamedRenameInSVNCheckBox.Checked = false;
                onItemRemovedRemoveFromSVNCheckBox.Checked = false;
                closeOnEndCheckBox.Checked = false;
                okButton.Enabled = false;
                browseButton.Enabled = false;
            }

            if (string.IsNullOrEmpty(rootFolderTextBox.Text))
            {
                rootFolderTextBox.Text = await CommandHelper.GetRepositoryRoot();
            }
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void OkButton_Click(object sender, EventArgs e)
            => Save().FireAndForget();

        private async Task Save()
        {
            options.RootFolder = rootFolderTextBox.Text;
            options.OnItemAddedAddToSVN = onItemAddedAddToSVNCheckBox.Checked;
            options.OnItemRenamedRenameInSVN = onItemRenamedRenameInSVNCheckBox.Checked;
            options.OnItemRemovedRemoveFromSVN = onItemRemovedRemoveFromSVNCheckBox.Checked;
            options.CloseOnEnd = closeOnEndCheckBox.Checked;
            await OptionsHelper.SaveOptions(options);

            DiffMarkersState.IgnoreWhitespace = ignoreWhitespaceCheckBox.Checked;
            DiffMarkersState.ShowOverviewBar = showOverviewBarCheckBox.Checked;
            Close();
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
            {
                rootFolderTextBox.Text = folderBrowserDialog1.SelectedPath;
            }
        }
    }
}
