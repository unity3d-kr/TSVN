using SamirBoulema.TSVN.Helpers;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SamirBoulema.TSVN.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Task = System.Threading.Tasks.Task;
using Settings = SamirBoulema.TSVN.Properties.Settings;
using File = System.IO.File;
using Community.VisualStudio.Toolkit;

namespace SamirBoulema.TSVN
{
    public partial class TSVNToolWindowControl : UserControl
    {
        /// <summary>
        /// Icons are resolved per extension instead of per file. Extracting one costs a shell
        /// round trip, and a large working copy has thousands of files sharing a handful of
        /// extensions.
        /// </summary>
        private static readonly Dictionary<string, ImageSource> FileIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many entries are added between two UI yields while the list is built.</summary>
        private const int TreeBuildBatchSize = 100;

        public PendingChangesViewModel ViewModel;

        private CancellationTokenSource _loadCancellation;
        private bool _busy;
        private bool _hasRepository;

        public TSVNToolWindowControl()
        {
            Loaded += OnLoaded;

            InitializeComponent();

            ViewModel = new PendingChangesViewModel();

            DataContext = ViewModel;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            VS.Events.DocumentEvents.Saved += DocumentEvents_Saved;

            VS.Events.SolutionEvents.OnAfterOpenSolution += SolutionEvents_OnAfterOpenSolution1;

            HideUnversionedButton.IsChecked = Settings.Default.HideUnversioned;

            Reload();
        }

        private void DocumentEvents_Saved(string obj)
        {
            Reload();
        }

        private void SolutionEvents_OnAfterOpenSolution1(SolutionItem obj)
        {
            Reload();
        }

        private void Reload() => ReloadAsync().FireAndForget();

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ReportProgress("Cancelling...", 0, 0);
            CancelRunningLoad();
        }

        /// <summary>
        /// Loads the pending changes with a progress report and a way out. Cancelling kills the
        /// <c>svn status</c> process tree and leaves the list that is already on screen untouched.
        /// </summary>
        private async Task ReloadAsync()
        {
            // A save event can arrive while a previous scan is still running.
            CancelRunningLoad();

            var cts = new CancellationTokenSource();
            _loadCancellation = cts;

            BeginBusy();

            try
            {
                var repositoryRoot = await CommandHelper.GetRepositoryRoot();

                cts.Token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(repositoryRoot))
                {
                    ShowTree(new List<string>(), null);
                    _hasRepository = false;
                    return;
                }

                var progress = new Progress<int>(count =>
                    ReportProgress($"Reading svn status... {count:N0} item(s) found", 0, 0));

                var pendingChanges = await CommandHelper.GetPendingChanges(repositoryRoot, progress, cts.Token);

                cts.Token.ThrowIfCancellationRequested();

                await ShowTreeAsync(pendingChanges, repositoryRoot, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Whatever was on screen before the cancel stays there.
            }
            catch (Exception e)
            {
                LogHelper.Log("PendingChanges.Reload", e);
            }
            finally
            {
                if (ReferenceEquals(_loadCancellation, cts))
                {
                    _loadCancellation = null;
                    EndBusy();
                }

                cts.Dispose();
            }
        }

        private void CancelRunningLoad()
        {
            var cts = _loadCancellation;

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }

        private void BeginBusy()
        {
            _busy = true;

            progressPanel.Visibility = Visibility.Visible;
            progressText.Text = "Reading svn status...";
            progressBar.IsIndeterminate = true;
            progressBar.Value = 0;

            commitButton.IsEnabled = false;
            revertButton.IsEnabled = false;
            refreshButton.IsEnabled = false;
        }

        private void EndBusy()
        {
            _busy = false;

            progressPanel.Visibility = Visibility.Collapsed;
            refreshButton.IsEnabled = true;
            commitButton.IsEnabled = _hasRepository;
            revertButton.IsEnabled = _hasRepository;
        }

        private void ReportProgress(string text, double value, double maximum)
        {
            if (!_busy)
            {
                return;
            }

            progressText.Text = text;

            if (maximum > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Maximum = maximum;
                progressBar.Value = Math.Min(value, maximum);
            }
            else
            {
                progressBar.IsIndeterminate = true;
            }
        }

        private async System.Threading.Tasks.Task ShowTreeAsync(
            List<string> pendingChanges,
            string solutionDir,
            CancellationToken cancellationToken)
        {
            var solutionDirItem = CreateFolderTreeViewItem(solutionDir, "S");

            for (var i = 0; i < pendingChanges.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ProcessChange(solutionDirItem, solutionDir, pendingChanges[i]);

                if ((i + 1) % TreeBuildBatchSize != 0)
                {
                    continue;
                }

                ReportProgress(
                    $"Building the list... {i + 1:N0} / {pendingChanges.Count:N0}",
                    i + 1,
                    pendingChanges.Count);

                // Back to the dispatcher so the bar repaints and a Cancel click is handled.
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();

            ShowTree(pendingChanges, solutionDir, solutionDirItem);
        }

        public void ShowTree(List<string> pendingChanges, string solutionDir, TSVNTreeViewFolderItem solutionDirItem = null)
        {
            ViewModel.Root.Clear();

            if (string.IsNullOrEmpty(solutionDir))
            {
                _hasRepository = false;
                return;
            }

            _hasRepository = true;

            var root = new TSVNTreeViewFolderItem
            {
                Label = $"Changes ({pendingChanges.Count})",
                Foreground = ToBrush(EnvironmentColors.ToolWindowTextColorKey),
                IsExpanded = true
            };

            if (solutionDirItem == null)
            {
                solutionDirItem = CreateFolderTreeViewItem(solutionDir, "S");

                foreach (var change in pendingChanges)
                {
                    ProcessChange(solutionDirItem, solutionDir, change);
                }
            }

            root.Items.Add(solutionDirItem);

            ViewModel.Root.Add(root);
        }

        private void ProcessChange(TSVNTreeViewFolderItem root, string solutionDir, string change)
        {
            if (change.Length <= 8)
            {
                return;
            }

            var path = change.Substring(8);
            var pathParts = path.Split('\\');

            for (var i = 0; i < pathParts.Length; i++)
            {
                var item = FindItem(root, pathParts[i]);
                if (item == null)
                {
                    TSVNTreeViewItem newItem;
                    if (i == pathParts.Length - 1 && File.Exists(Path.Combine(solutionDir, path)))
                    {
                        newItem = CreateFileTreeViewItem(pathParts[i], solutionDir, path, change);
                    }
                    else
                    {
                        newItem = CreateFolderTreeViewItem(pathParts[i], change);
                    }
                        
                    root.Items.Add(newItem);

                    root = newItem as TSVNTreeViewFolderItem;
                }
                else if (item is TSVNTreeViewFolderItem folderItem)
                {
                    root = folderItem;
                }
            }
        }

        private TSVNTreeViewItem FindItem(TSVNTreeViewFolderItem root, string text)
        {
            if (root?.Items == null)
            {
                return null;
            }

            foreach (var item in root.Items)
            {
                if (item.Label.Equals(text))
                {
                    return item;
                }
            }

            return null;
        }

        private TSVNTreeViewItem CreateFileTreeViewItem(string text, string solutionDir, string path, string change)
        {
            var filePath = Path.Combine(solutionDir, path);

            var item = new TSVNTreeViewFileItem
            {
                Path = filePath,
                Tooltip = $"Name: {text}\nFolder: {Path.GetDirectoryName(filePath)}\nType: {GetTypeOfChange(change[0])}",
                Label = text,
                ChangeType = GetTypeOfChangeShort(change[0]),
                PendingIconSource = GetPendingIconOfChange(change[0]),
                PendingTooltip = GetPendingTooltipOfChange(change[0]),
                FileIconSource = GetFileIcon(filePath),
                FileIconVisibility = File.Exists(filePath) ? Visibility.Visible : Visibility.Collapsed,
                IconVisibility = File.Exists(filePath) ? Visibility.Collapsed : Visibility.Visible,
                IconSource = "Resources\\XAML\\Document_16x.xaml",
                Foreground = ToBrush(EnvironmentColors.ToolWindowTextColorKey)
            };

            return item;
        }

        private string GetTypeOfChange(char change)
        {
            switch (change)
            {
                case 'A': return "Add";
                case 'D': return "Delete";
                case 'R': return "Replaced";
                case '!': return "Missing";
                case 'M': return "Modified";
                case '?': return "Unversioned";
                default: return string.Empty;
            }
        }

        private string GetTypeOfChangeShort(char change)
        {
            switch (change)
            {
                case 'A': return "[add]";
                case 'D': return "[del]";
                case 'R': return "[rep]";
                case '!': return "[mis]";
                case 'M': return "[mod]";
                case '?': return "[unv]";
                default: return string.Empty;
            }
        }

        private string GetPendingIconOfChange(char change)
        {
            switch (change)
            {
                case 'A': return "Resources\\XAML\\PendingAdd.xaml";
                case 'D': return "Resources\\XAML\\PendingDelete.xaml";
                case 'M': return "Resources\\XAML\\PendingEdit.xaml";
                default: return "Resources\\XAML\\CheckedIn.xaml";
            }
        }

        private string GetPendingTooltipOfChange(char change)
        {
            switch (change)
            {
                case 'A': return "Pending add";
                case 'D': return "Pending delete";
                case 'M': return "Pending edit";
                default: return "Checked in";
            }
        }

        private TSVNTreeViewFolderItem CreateFolderTreeViewItem(string text, string change)
        {
            return new TSVNTreeViewFolderItem
            {
                Label = text,
                ChangeType = GetTypeOfChangeShort(change[0]),
                Foreground = new SolidColorBrush(Colors.Gray),
                IsExpanded = true,
                FileIconVisibility = Visibility.Collapsed,
                IconVisibility = Visibility.Visible
            };
        }

        /// <summary>
        /// Convert from VSTheme EnvironmentColor to a XAML SolidColorBrush
        /// </summary>
        /// <param name="key">VSTheme EnvironmentColor key</param>
        /// <returns>XAML SolidColorBrush</returns>
        private static SolidColorBrush ToBrush(ThemeResourceKey key)
        {
            var color = VSColorTheme.GetThemedColor(key);
            return new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        }

        private static ImageSource ToImageSource(Icon icon)
        {
            ImageSource imageSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            return imageSource;
        }

        private void CommitButton_Click(object sender, RoutedEventArgs e) => CommandHelper.Commit().FireAndForget();

        private void RevertButton_Click(object sender, RoutedEventArgs e) => CommandHelper.Revert().FireAndForget();

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void HideUnversionedButton_OnChecked(object sender, RoutedEventArgs e) => ToggleUnversioned(true).FireAndForget();

        private void HideUnversionedButton_OnUnchecked(object sender, RoutedEventArgs e) => ToggleUnversioned(false).FireAndForget();

        private async Task ToggleUnversioned(bool hide)
        {
            Settings.Default.HideUnversioned = hide;
            Settings.Default.Save();
            await ReloadAsync();
        }

        /// <summary>
        /// Resolves the shell icon for a file, reusing the one already resolved for its extension.
        /// Extracting an icon is a shell round trip, so a large working copy paid for it thousands
        /// of times when it only needed one per file type.
        /// </summary>
        private static ImageSource GetFileIcon(string filePath)
        {
            var key = Path.GetExtension(filePath);

            if (string.IsNullOrEmpty(key))
            {
                key = Path.GetFileName(filePath);
            }

            ImageSource cached;

            if (!string.IsNullOrEmpty(key) && FileIconCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            ImageSource icon = null;

            try
            {
                if (File.Exists(filePath))
                {
                    using (var extracted = Icon.ExtractAssociatedIcon(filePath))
                    {
                        if (extracted != null)
                        {
                            icon = ToImageSource(extracted);

                            if (icon != null && icon.CanFreeze)
                            {
                                icon.Freeze();
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A file the shell has no icon for is no reason to stop building the list.
            }

            if (!string.IsNullOrEmpty(key))
            {
                FileIconCache[key] = icon;
            }

            return icon;
        }

        private void TreeView_Collapsed(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem item &&
                item.DataContext is TSVNTreeViewFolderItem tsvnFolderItem)
            {
                tsvnFolderItem.IconSource = "Resources\\XAML\\Folder_16x.xaml";
            }
        }

        private void TreeView_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem item &&
                item.DataContext is TSVNTreeViewFolderItem tsvnFolderItem)
            {
                tsvnFolderItem.IconSource = "Resources\\XAML\\FolderOpen_16x.xaml";
            }
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var filePath = ((e.OriginalSource as FrameworkElement).DataContext as TSVNTreeViewItem)?.Path;
            _ = FileHelper.OpenFile(filePath);
        }

        private void Commit_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ((e.OriginalSource as MenuItem).DataContext as TSVNTreeViewItem).Path;
            CommandHelper.Commit(filePath).FireAndForget();
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ((e.OriginalSource as MenuItem).DataContext as TSVNTreeViewItem).Path;
            CommandHelper.Revert(filePath).FireAndForget();
        }

        private void ShowDifferences_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ((e.OriginalSource as MenuItem).DataContext as TSVNTreeViewItem).Path;
            CommandHelper.ShowDifferences(filePath).FireAndForget();
        }
    }
}