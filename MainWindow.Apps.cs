using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using QuestTool.Core;

namespace QuestTool;

public partial class MainWindow
{
    readonly ObservableCollection<InstalledApp> installedApps = [];
    Headset? appsHeadset;
    bool selectingApps;

    async void AppHeadsetChanged(object sender, SelectionChangedEventArgs e)
    {
        appsHeadset = null;
        installedApps.Clear();
        if (AppsStatus is null) return;
        AppsStatus.Text = "Choose a headset to list its installed apps.";
        if (!selectingRefreshTargets && IsLoaded && AppsTab.IsSelected) await RefreshAppsAsync();
    }

    Task RefreshAppsAsync() => QueueRefreshAsync("Reading installed apps…", ReadAppsAsync);
    async void AppRefreshClick(object sender, RoutedEventArgs e) => await RefreshAppsAsync();
    async void AppSystemChanged(object sender, RoutedEventArgs e)
    {
        appsHeadset = null; installedApps.Clear();
        if (IsLoaded) await RefreshAppsAsync();
    }

    void AppSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (AppsGrid is null) return;
        string search = AppSearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(installedApps).Filter = item => item is InstalledApp app && app.Package.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    async Task ReadAppsAsync(CancellationToken token)
    {
        if (AppHeadsetBox.SelectedItem is not Headset headset) return;
        bool includeSystem = AppSystemBox.IsChecked == true;
        var service = new AppService(Adb);
        var entries = await service.ListAsync(HeadsetTarget(headset, 0), includeSystem, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(AppHeadsetBox.SelectedItem, headset) || includeSystem != (AppSystemBox.IsChecked == true)) return;
        string? selected = (AppsGrid.SelectedItem as InstalledApp)?.Package;
        selectingApps = true;
        try
        {
            installedApps.Clear();
            foreach (var app in entries) installedApps.Add(app);
            appsHeadset = headset;
            AppsGrid.SelectedItem = installedApps.FirstOrDefault(app => app.Package == selected);
        }
        finally { selectingApps = false; }
        AppsStatus.Text = $"{headset.Name} · {entries.Count} installed app(s). Click an app to expand its details.";
        if (AppsGrid.SelectedItem is InstalledApp current) await ReadAppDetailsAsync(headset, current, token);
    }

    async Task ReadAppDetailsAsync(Headset headset, InstalledApp app, CancellationToken token)
    {
        app.Details = "Loading app details…";
        try { app.Details = await new AppService(Adb).ReadDetailsAsync(HeadsetTarget(headset, 0), app.Package, token); }
        catch (OperationCanceledException) { app.Details = "Details loading cancelled. Select the app again or refresh."; throw; }
        catch (Exception ex) { app.Details = ex.Message; throw; }
    }

    async void AppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selectingApps || !ReferenceEquals(e.OriginalSource, AppsGrid)) return;
        if (AppsGrid.SelectedItem is InstalledApp app && appsHeadset is { } headset && ReferenceEquals(headset, AppHeadsetBox.SelectedItem))
            await QueueRefreshAsync("Reading app details…", token => ReadAppDetailsAsync(headset, app, token));
    }

    async void AppActionClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || loadingFiles || finishingMedia || sender is not Button { DataContext: InstalledApp app, Tag: string action } || appsHeadset is not { } headset || !ReferenceEquals(headset, AppHeadsetBox.SelectedItem)) return;
        if (action == "copy")
        {
            try { Clipboard.SetText(app.Package); Status("Copied package name: " + app.Package); }
            catch (Exception ex) { Status("Could not copy package name: " + ex.Message); }
            return;
        }
        if (action == "logs")
        {
            DeviceLogHeadsetBox.SelectedItem = headset;
            deviceLogPackage = app.Package;
            capturedDeviceLogs = ""; DeviceLogBox.Clear(); DeviceLogFilterBox.Clear();
            UpdateDeviceLogScope();
            Tabs.SelectedItem = DeviceLogsTab;
            await RefreshDeviceLogsAsync();
            return;
        }
        string? exportFolder = null;
        if (action == "export")
        {
            var picker = new OpenFolderDialog { Title = "Export APK files for " + app.Package };
            if (picker.ShowDialog(this) != true) return;
            exportFolder = picker.FolderName;
        }
        string? warning = action switch
        {
            "uninstall" => "Remove this app and its data for the current headset user? This cannot be undone.",
            "data" => "Reset this app for the current headset user? Saved settings, sign-ins, and progress will be deleted. This cannot be undone.",
            "cache" => "Remove this app's temporary cache for the current headset user? The app may need to download cached content again. Saved app data will be retained.",
            _ => null
        };
        string title = action switch
        {
            "launch" => "Launch app", "close" => "Close app", "restart" => "Restart app", "settings" => "Open app settings",
            "export" => "Export APK", "uninstall" => "Uninstall app", "data" => "Clear app data", "cache" => "Clear cache", _ => ""
        };
        if (title.Length == 0) return;
        if (warning is not null && MessageBox.Show(this, $"{app.Package}\nHeadset: {headset.Name} ({headset.Serial})\n\n{warning}", title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunOperationAsync($"{title} · {app.Package} on {headset.Name}…", async token =>
        {
            var service = new AppService(Adb);
            var target = HeadsetTarget(headset, 0);
            string result;
            switch (action)
            {
                case "launch": await service.LaunchAsync(target, app.Package, token); result = "App launched."; break;
                case "close": await service.CloseAsync(target, app.Package, token); result = "App closed."; break;
                case "restart": await service.RestartAsync(target, app.Package, token); result = "App restarted."; break;
                case "settings": await service.OpenSettingsAsync(target, app.Package, token); result = "App settings opened inside the headset."; break;
                case "data": await service.ClearAsync(target, app.Package, false, token); result = "App data cleared."; break;
                case "cache": await service.ClearAsync(target, app.Package, true, token); result = "Cache cleared; saved app data retained."; break;
                case "uninstall":
                    await service.UninstallAsync(target, app.Package, token);
                    installedApps.Remove(app);
                    result = "App uninstalled for the current headset user.";
                    break;
                case "export":
                    bool exporting = true;
                    var progress = new Progress<string>(message => { if (exporting) { AppsStatus.Text = message; Status(message); } });
                    string folder;
                    try { folder = await service.ExportAsync(target, app.Package, exportFolder!, progress, token); }
                    finally { exporting = false; }
                    result = "APK files exported to " + folder + ". App data and OBB files are not included.";
                    break;
                default: throw new InvalidOperationException("Unknown app action.");
            }
            AppsStatus.Text = $"{headset.Name} · {app.Package} · {result}";
            Status(AppsStatus.Text); Log(AppsStatus.Text);
        });
    }
}
