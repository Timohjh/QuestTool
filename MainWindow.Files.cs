using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuestTool.Core;

namespace QuestTool;

public partial class MainWindow
{
    readonly ObservableCollection<HeadsetFile> headsetFiles = [];
    Headset? browsedHeadset;
    string browsedPath = FileService.Root;

    DeploymentTarget FileTarget(Headset headset) => HeadsetTarget(headset, FileConnectionBox.SelectedIndex);

    static DeploymentTarget HeadsetTarget(Headset headset, int connectionMode)
    {
        string transport, fallback;
        switch (connectionMode)
        {
            case 1:
                if (headset.UsbTransport.Length == 0) throw new IOException("No verified USB connection. Plug in the headset and Refresh headsets first.");
                transport = headset.UsbTransport; fallback = ""; break;
            case 2:
                if (headset.Endpoint.Length == 0) throw new IOException("No Wi-Fi address. Enable Wi-Fi in Headsets first.");
                transport = headset.Endpoint; fallback = ""; break;
            default:
                transport = headset.UsbTransport.Length > 0 ? headset.UsbTransport : headset.Transport;
                fallback = headset.Endpoint; break;
        }
        return new(headset.Serial, transport, fallback);
    }

    async void FileConnectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && FilesTab.IsSelected && FileHeadsetBox.SelectedItem is Headset && operation is null && !loadingFiles)
            await OpenDirectoryAsync(browsedPath);
    }

    async void FileHeadsetChanged(object sender, SelectionChangedEventArgs e)
    {
        browsedHeadset = null; browsedPath = FileService.Root;
        headsetFiles.Clear();
        if (FilePathBox is null || FileBrowserStatus is null) return;
        FilePathBox.Text = FileService.Root;
        FileBrowserStatus.Text = "Choose a headset to browse its shared storage.";
        if (!selectingRefreshTargets && FileHeadsetBox.SelectedItem is Headset && FilesTab.IsSelected) await OpenDirectoryAsync(FileService.Root);
    }

    async Task RunFileOperationAsync(string title, Func<FileService, Headset, CancellationToken, Task> action)
    {
        if (FileHeadsetBox.SelectedItem is not Headset headset) { Status("Choose a headset in Files first."); return; }
        await RunOperationAsync(title, async token =>
        {
            FileBrowserStatus.Text = title;
            try { await action(new FileService(Adb), headset, token); }
            catch (OperationCanceledException) { FileBrowserStatus.Text = "Cancelled. Completed changes are retained; refresh the folder before continuing."; throw; }
            catch (Exception ex) { FileBrowserStatus.Text = ex.Message; throw; }
        });
    }

    async Task ReadDirectoryAsync(FileService service, Headset headset, string path, CancellationToken token)
    {
        path = FileService.NormalizePath(path);
        int connectionMode = FileConnectionBox.SelectedIndex;
        var entries = await service.ListAsync(FileTarget(headset), path, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(FileHeadsetBox.SelectedItem, headset) || connectionMode != FileConnectionBox.SelectedIndex) return;
        headsetFiles.Clear();
        foreach (var entry in entries) headsetFiles.Add(entry);
        browsedHeadset = headset; browsedPath = path; FilePathBox.Text = path;
        FileBrowserStatus.Text = $"{headset.Name} · {entries.Count} item(s) · {path}";
        Status("Folder loaded. " + FileBrowserStatus.Text);
    }

    async Task OpenDirectoryAsync(string path)
    {
        if (FileHeadsetBox.SelectedItem is not Headset headset) { Status("Choose a headset in Files first."); return; }
        await QueueRefreshAsync("Opening headset folder…", token => ReadDirectoryAsync(new FileService(Adb), headset, path, token));
    }
    async void FileGoClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(FilePathBox.Text);
    async void FileRefreshClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(browsedPath);
    async void FileRootClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(FileService.Root);
    async void FileUpClick(object sender, RoutedEventArgs e) => await OpenDirectoryAsync(FileService.ParentPath(browsedPath));
    async void FilePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenDirectoryAsync(FilePathBox.Text);
    }
    async void FileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) is DataGridRow { Item: HeadsetFile { IsDirectory: true } entry })
            await OpenDirectoryAsync(entry.Path);
    }

    bool HasFileFolder()
    {
        if (browsedHeadset is not null && ReferenceEquals(browsedHeadset, FileHeadsetBox.SelectedItem)) return true;
        Status("Open a folder on the chosen headset first."); return false;
    }

    HeadsetFile? SelectedFile()
    {
        if (!HasFileFolder()) return null;
        if (FilesGrid.SelectedItem is HeadsetFile { Kind: "File" or "Folder" } entry) return entry;
        Status("Select a file or folder. Symbolic links are shown for reference only."); return null;
    }

    string? AskFileText(string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 380 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 500 });
        panel.Children.Add(input); panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Owner = this, Icon = Icon, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize, ShowInTaskbar = false };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    async void NewFolderClick(object sender, RoutedEventArgs e)
    {
        if (!HasFileFolder()) return;
        string? name = AskFileText("New folder", $"Folder name in {browsedPath}");
        if (name is null) return;
        string parent = browsedPath;
        await RunFileOperationAsync("Creating folder…", async (service, headset, token) =>
        {
            await service.CreateFolderAsync(FileTarget(headset), parent, name, token);
            Log($"{headset.Name}: Created {parent}/{name}");
            await ReadDirectoryAsync(service, headset, parent, token);
        });
    }

    async void RenameFileClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedFile(); if (entry is null) return;
        string? name = AskFileText("Rename " + entry.Kind.ToLowerInvariant(), "New name for " + entry.Name, entry.Name);
        if (name is null || name == entry.Name) return;
        string parent = browsedPath;
        await RunFileOperationAsync("Renaming item…", async (service, headset, token) =>
        {
            await service.MoveAsync(FileTarget(headset), entry.Path, FileService.ChildPath(parent, name), token);
            Log($"{headset.Name}: Renamed {entry.Path} to {name}");
            await ReadDirectoryAsync(service, headset, parent, token);
        });
    }

    async void MoveFileClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedFile(); if (entry is null) return;
        string? destination = AskFileText("Move " + entry.Kind.ToLowerInvariant(), $"Destination folder for {entry.Name} on this headset", browsedPath);
        if (destination is null) return;
        string parent = browsedPath;
        await RunFileOperationAsync("Moving item…", async (service, headset, token) =>
        {
            await service.MoveAsync(FileTarget(headset), entry.Path, FileService.ChildPath(destination, entry.Name), token);
            Log($"{headset.Name}: Moved {entry.Path} to {destination}");
            await ReadDirectoryAsync(service, headset, parent, token);
        });
    }

    async void DeleteFileClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedFile(); if (entry is null) return;
        if (MessageBox.Show(this, $"Delete {entry.Path} from {browsedHeadset!.Name}?" + (entry.IsDirectory ? "\n\nThe folder and everything inside it will be deleted." : "") + "\n\nThis cannot be undone.", "Delete " + entry.Kind.ToLowerInvariant(), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string parent = browsedPath;
        await RunFileOperationAsync("Deleting item…", async (service, headset, token) =>
        {
            await service.DeleteAsync(FileTarget(headset), entry.Path, token);
            Log($"{headset.Name}: Deleted {entry.Path}");
            await ReadDirectoryAsync(service, headset, parent, token);
        });
    }

    async void UploadFilesClick(object sender, RoutedEventArgs e)
    {
        if (!HasFileFolder()) return;
        var picker = new OpenFileDialog { Title = "Upload files to " + browsedHeadset!.Name, Multiselect = true, Filter = "All files|*.*" };
        if (picker.ShowDialog(this) == true) await UploadItemsAsync(picker.FileNames);
    }

    async void UploadFolderClick(object sender, RoutedEventArgs e)
    {
        if (!HasFileFolder()) return;
        var picker = new OpenFolderDialog { Title = "Upload folder to " + browsedHeadset!.Name };
        if (picker.ShowDialog(this) == true) await UploadItemsAsync([picker.FolderName]);
    }

    async Task UploadItemsAsync(string[] paths)
    {
        string parent = browsedPath;
        await RunFileOperationAsync("Uploading to headset…", async (service, headset, token) =>
        {
            var existing = await service.ListAsync(FileTarget(headset), parent, token);
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                var conflict = existing.FirstOrDefault(f => f.Name == name);
                bool replace = false;
                if (conflict is not null)
                {
                    if (conflict.Kind != "File" || Directory.Exists(path)) throw new IOException($"{name} already exists. Rename it or choose another destination folder.");
                    replace = MessageBox.Show(this, $"Replace {conflict.Path} on {headset.Name} with the selected local file?", "Replace file", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                    if (!replace) continue;
                }
                Status("Uploading " + name + " to " + headset.Name + "…");
                FileBrowserStatus.Text = StatusText.Text;
                await service.UploadAsync(FileTarget(headset), parent, path, replace, token);
                Log($"{headset.Name}: Uploaded {parent}/{name}");
            }
            await ReadDirectoryAsync(service, headset, parent, token);
        });
    }

    async void DownloadFileClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedFile(); if (entry is null) return;
        if (entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name.EndsWith('.') || entry.Name.EndsWith(' '))
        { Status("This name is not supported on Windows. Rename the item before downloading it."); return; }
        string destination;
        if (entry.IsDirectory)
        {
            var picker = new OpenFolderDialog { Title = "Choose the local parent folder for " + entry.Name };
            if (picker.ShowDialog(this) != true) return;
            destination = Path.Combine(picker.FolderName, entry.Name);
        }
        else
        {
            var picker = new SaveFileDialog { Title = "Download file (choose a new local name)", FileName = entry.Name, Filter = "All files|*.*", OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return;
            destination = picker.FileName;
        }
        await RunFileOperationAsync("Downloading " + entry.Name + "…", async (service, headset, token) =>
        {
            FileTransferBar.Value = 0; FileTransferBar.IsIndeterminate = entry.IsDirectory;
            FileTransferBar.Visibility = Visibility.Visible;
            bool receiving = true;
            var progress = new Progress<FileTransferProgress>(update =>
            {
                if (!receiving) return;
                double speed = update.Elapsed.TotalSeconds > 0 ? update.Bytes / update.Elapsed.TotalSeconds : 0;
                FileTransferBar.Value = update.TotalBytes > 0 ? Math.Min(100, update.Bytes * 100d / update.TotalBytes) : 0;
                FileBrowserStatus.Text = $"Downloading {entry.Name} · {update.Bytes / 1048576d:N1} / {update.TotalBytes / 1048576d:N1} MB · {speed / 1048576d:N1} MB/s";
                Status(FileBrowserStatus.Text);
            });
            try
            {
                await service.DownloadAsync(FileTarget(headset), entry, destination, token, progress);
                Log($"{headset.Name}: Downloaded {entry.Path} to {destination}");
                FileBrowserStatus.Text = "Downloaded to " + destination;
                Status(FileBrowserStatus.Text);
            }
            finally { receiving = false; FileTransferBar.Visibility = Visibility.Collapsed; }
        });
    }
}
