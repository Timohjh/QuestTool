using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace QuestTool;

public partial class MainWindow
{
    Task? refreshTask;
    CancellationTokenSource? refreshCancellation;
    (string Title, Func<CancellationToken, Task> Action)? pendingRefresh;
    long? unfocusedSince;
    bool selectingRefreshTargets;
    bool closingWindow;

    void InitializeRefresh()
    {
        Tabs.SelectionChanged += RefreshTabSelected;
        Deactivated += (_, _) => unfocusedSince ??= Stopwatch.GetTimestamp();
        Activated += async (_, _) =>
        {
            var since = unfocusedSince;
            unfocusedSince = null;
            if (IsLoaded && since.HasValue && Stopwatch.GetElapsedTime(since.Value) >= TimeSpan.FromSeconds(60))
                await RefreshCurrentTabAsync();
        };
    }

    Task QueueRefreshAsync(string title, Func<CancellationToken, Task> action)
    {
        if (closingWindow || !File.Exists(settings.AdbPath)) return Task.CompletedTask;
        // Keep only the latest request while another refresh or an operation finishes.
        pendingRefresh = (title, action);
        return ResumeRefreshAsync();
    }

    Task ResumeRefreshAsync()
    {
        if (closingWindow || operation is not null || loadingFiles || finishingMedia || pendingRefresh is null)
            return Task.CompletedTask;
        return refreshTask ??= ProcessRefreshesAsync();
    }

    async Task ProcessRefreshesAsync()
    {
        // Publish refreshTask before running callbacks that can raise selection events.
        await Task.Yield();
        RefreshProgress.Visibility = Visibility.Visible;
        try
        {
            while (!closingWindow && operation is null && !loadingFiles && !finishingMedia && pendingRefresh is { } request)
            {
                pendingRefresh = null;
                using var cancellation = new CancellationTokenSource();
                refreshCancellation = cancellation;
                RefreshStatus.Text = request.Title;
                try { await request.Action(cancellation.Token); SaveSettings(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) { Status("Refresh failed: " + ex.Message); Log(ex.ToString()); }
                finally { refreshCancellation = null; UpdateCount(); }
            }
        }
        finally
        {
            refreshTask = null;
            RefreshProgress.Visibility = Visibility.Collapsed;
            RefreshStatus.Text = "";
        }
    }

    async Task StopRefreshAsync()
    {
        refreshCancellation?.Cancel();
        if (refreshTask is { } running) await running;
    }

    async void RefreshTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs) && IsLoaded) await RefreshCurrentTabAsync();
    }

    Task RefreshCurrentTabAsync() => QueueRefreshAsync("Refreshing…", async token =>
    {
        await RefreshDevicesAsync(token, true, false);
        token.ThrowIfCancellationRequested();
        var headset = headsets.FirstOrDefault(h => h.Connection != "Offline" && h.Selected)
            ?? headsets.FirstOrDefault(h => h.Connection != "Offline") ?? headsets.FirstOrDefault();
        selectingRefreshTargets = true;
        try
        {
            if (FilesTab.IsSelected && FileHeadsetBox.SelectedItem is null) FileHeadsetBox.SelectedItem = headset;
            if (AppsTab.IsSelected && AppHeadsetBox.SelectedItem is null) AppHeadsetBox.SelectedItem = headset;
            if (MediaTab.IsSelected && MediaHeadsetBox.SelectedItem is null) MediaHeadsetBox.SelectedItem = headset;
            if (DeviceLogsTab.IsSelected && DeviceLogHeadsetBox.SelectedItem is null) DeviceLogHeadsetBox.SelectedItem = headset;
            if (WifiTab.IsSelected && WifiHeadsetBox.SelectedItem is null) WifiHeadsetBox.SelectedItem = headset;
        }
        finally { selectingRefreshTargets = false; }

        if (MonitorTab.IsSelected) await ReadMonitoringAsync(token);
        else if (AppsTab.IsSelected) await ReadAppsAsync(token);
        else if (DeployTab.IsSelected) await ReadVersionsAsync(token);
        else if (FilesTab.IsSelected && FileHeadsetBox.SelectedItem is Core.Headset fileHeadset)
            await ReadDirectoryAsync(new Core.FileService(Adb), fileHeadset, browsedPath, token);
        else if (DeviceLogsTab.IsSelected) await ReadDeviceLogsAsync(token);
        else if (WifiTab.IsSelected) await ReadWifiAsync(token);
        else if (MediaTab.IsSelected && (StreamingBackendBox.SelectedIndex == 1 || File.Exists(settings.MetaVrPath)))
            await ReadStreamingStatusAsync(token);
    });
}
