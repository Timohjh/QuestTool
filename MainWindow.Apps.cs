using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    async void LaunchAppClick(object sender, RoutedEventArgs e) => await RunAppActionAsync(sender, true);
    async void CloseAppClick(object sender, RoutedEventArgs e) => await RunAppActionAsync(sender, false);

    async Task RunAppActionAsync(object sender, bool launch)
    {
        if (sender is not Button { DataContext: InstalledApp app } || appsHeadset is not { } headset || !ReferenceEquals(headset, AppHeadsetBox.SelectedItem)) return;
        await RunOperationAsync($"{(launch ? "Launching" : "Closing")} {app.Package} on {headset.Name}…", async token =>
        {
            var service = new AppService(Adb);
            var target = HeadsetTarget(headset, 0);
            if (launch) await service.LaunchAsync(target, app.Package, token);
            else await service.CloseAsync(target, app.Package, token);
            AppsStatus.Text = $"{headset.Name} · {app.Package} {(launch ? "launched" : "closed")}.";
            Status(AppsStatus.Text); Log(AppsStatus.Text);
        });
    }
}
