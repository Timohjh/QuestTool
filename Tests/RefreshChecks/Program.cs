using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuestTool.Core;

namespace QuestTool;

static class Program
{
    [STAThread]
    static int Main()
    {
        var app = new Application();
        int result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            var window = new MainWindow();
            try { window.Show(); await window.VerifyAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { window.Close(); app.Shutdown(); }
        });
        app.Run();
        return result;
    }
}

// Exercise the production refresh partial on a real WPF dispatcher, with in-memory
// device reads and settings persistence so no headset or user settings are changed.
public partial class MainWindow : Window
{
    readonly Settings settings = new() { AdbPath = Environment.ProcessPath! };
    readonly List<Headset> headsets = [new() { Serial = "TEST", Selected = true, Connection = "USB" }];
    readonly TabControl Tabs = new();
    readonly TabItem FilesTab = new(), MediaTab = new(), DeviceLogsTab = new(), WifiTab = new(), MonitorTab = new(), DeployTab = new(), AppsTab = new();
    readonly ComboBox FileHeadsetBox = new(), MediaHeadsetBox = new(), DeviceLogHeadsetBox = new(), WifiHeadsetBox = new(), AppHeadsetBox = new();
    readonly ProgressBar RefreshProgress = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock RefreshStatus = new();
    readonly string browsedPath = FileService.Root;
    readonly IAdb Adb = new UnusedAdb();
    readonly List<string> reads = [], failures = [];
    CancellationTokenSource? operation;
    bool loadingFiles, finishingMedia;
    int passed, selectedDuringRefresh;

    public MainWindow()
    {
        ShowActivated = false; ShowInTaskbar = false; Opacity = 0;
        Width = 1; Height = 1; WindowStyle = WindowStyle.None;
        foreach (var tab in new[] { new TabItem(), MonitorTab, AppsTab, FilesTab, MediaTab, DeviceLogsTab, WifiTab, DeployTab }) Tabs.Items.Add(tab);
        foreach (var box in new[] { FileHeadsetBox, MediaHeadsetBox, DeviceLogHeadsetBox, WifiHeadsetBox, AppHeadsetBox })
        {
            box.ItemsSource = headsets;
            box.SelectionChanged += (_, _) => { if (selectingRefreshTargets) selectedDuringRefresh++; };
        }
        StreamingBackendBox.Items.Add("Meta"); StreamingBackendBox.Items.Add("Mirror"); StreamingBackendBox.SelectedIndex = 1;
        Content = Tabs;
        InitializeRefresh();
    }

    void SaveSettings() { }
    void UpdateCount() { }
    void Status(string message) { }
    void Log(string message) => failures.Add(message);
    Task Record(string name) { reads.Add(name); return Task.CompletedTask; }
    Task RefreshDevicesAsync(CancellationToken token, bool reconnect, bool readMonitoring = true)
    {
        if (readMonitoring) throw new Exception("Tab refresh should load only the current tab's data.");
        return Record("devices");
    }
    Task ReadMonitoringAsync(CancellationToken token) => Record("monitoring");
    Task ReadAppsAsync(CancellationToken token) => Record("apps");
    Task ReadVersionsAsync(CancellationToken token) => Record("versions");
    Task ReadDirectoryAsync(FileService service, Headset headset, string path, CancellationToken token) => Record("files");
    Task ReadDeviceLogsAsync(CancellationToken token) => Record("logs");
    Task ReadWifiAsync(CancellationToken token) => Record("wifi");
    Task ReadStreamingStatusAsync(CancellationToken token) => Record("video");
    ComboBox StreamingBackendBox { get; } = new();

    void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS " + name); passed++;
    }

    public async Task VerifyAsync()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = QueueRefreshAsync("Slow refresh", async token => { started.SetResult(); await release.Task.WaitAsync(token); });
        await started.Task;
        Check(Tabs.IsEnabled && RefreshProgress.Visibility == Visibility.Visible, "Controls remain enabled and refresh progress is visible during a slow read");
        Check(!first.IsCompleted, "Refresh yields to the dispatcher before the read completes");
        int obsolete = 0, latest = 0;
        var second = QueueRefreshAsync("Obsolete", _ => { obsolete++; return Task.CompletedTask; });
        var third = QueueRefreshAsync("Latest", _ => { latest++; return Task.CompletedTask; });
        Check(ReferenceEquals(first, second) && ReferenceEquals(second, third), "Refresh requests share a single worker");
        release.SetResult();
        await first;
        Check(obsolete == 0 && latest == 1, "Repeated requests coalesce to the latest pending refresh");
        Check(refreshTask is null && RefreshProgress.Visibility == Visibility.Collapsed && RefreshStatus.Text == "", "Successful refresh clears its task and progress indicator");

        started = new();
        bool cancelled = false;
        first = QueueRefreshAsync("Cancellable", async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        await started.Task;
        operation = new();
        await StopRefreshAsync();
        Check(cancelled && first.IsCompleted && failures.Count == 0, "Foreground operation cancels and drains a running refresh without logging an error");
        latest = 0;
        await QueueRefreshAsync("Deferred", _ => { latest++; return Task.CompletedTask; });
        Check(latest == 0 && refreshTask is null, "Refresh waits while a foreground operation runs");
        operation.Dispose(); operation = null;
        await ResumeRefreshAsync();
        Check(latest == 1, "Deferred refresh resumes when the foreground operation finishes");

        first = QueueRefreshAsync("Before first yield", _ => { latest++; return Task.CompletedTask; });
        operation = new();
        await StopRefreshAsync();
        Check(first.IsCompleted && latest == 1, "Foreground operation can take priority before the refresh starts");
        operation.Dispose(); operation = null;
        await ResumeRefreshAsync();
        Check(latest == 2, "An interrupted request that has not started is retained");
        latest = 1;

        loadingFiles = true;
        await QueueRefreshAsync("Loading files", _ => { latest++; return Task.CompletedTask; });
        Check(latest == 1, "Refresh waits while local APK files are loading");
        loadingFiles = false;
        await ResumeRefreshAsync();
        Check(latest == 2, "Refresh resumes after local file loading");

        finishingMedia = true;
        await QueueRefreshAsync("Saving video", _ => Task.CompletedTask);
        Check(refreshTask is null, "Refresh waits while media cleanup is running");
        finishingMedia = false;
        await ResumeRefreshAsync();

        await QueueRefreshAsync("Failure", _ => throw new IOException("Expected test failure"));
        Check(failures.Count == 1 && refreshTask is null && RefreshProgress.Visibility == Visibility.Collapsed, "Failed refresh reports the error and releases the worker");
        await QueueRefreshAsync("Recovery", _ => { latest++; return Task.CompletedTask; });
        Check(latest == 3, "A later refresh succeeds after an earlier failure");

        foreach (var (tab, expected) in new[] { (MonitorTab, "monitoring"), (AppsTab, "apps"), (FilesTab, "files"), (MediaTab, "video"), (DeviceLogsTab, "logs"), (WifiTab, "wifi"), (DeployTab, "versions") })
        {
            reads.Clear();
            Tabs.SelectedItem = tab;
            await (refreshTask ?? Task.CompletedTask);
            Check(reads.SequenceEqual(new[] { "devices", expected }), "Opening a tab refreshes fleet and relevant data: " + expected);
        }
        Check(ReferenceEquals(FileHeadsetBox.SelectedItem, headsets[0]) && ReferenceEquals(WifiHeadsetBox.SelectedItem, headsets[0]) && selectedDuringRefresh == 5, "Opening a device tab selects an available headset under the selection-event guard");

        reads.Clear();
        unfocusedSince = Stopwatch.GetTimestamp() - 59 * Stopwatch.Frequency;
        OnActivated(EventArgs.Empty);
        Check(refreshTask is null && reads.Count == 0, "Returning after less than 60 seconds does not refresh");
        unfocusedSince = Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency;
        OnActivated(EventArgs.Empty);
        await (refreshTask ?? Task.CompletedTask);
        Check(reads.SequenceEqual(new[] { "devices", "versions" }), "Returning after 60 seconds refreshes the active tab");
        reads.Clear();
        OnActivated(EventArgs.Empty);
        Check(reads.Count == 0 && refreshTask is null, "Duplicate activation does not refresh again");
        OnDeactivated(EventArgs.Empty);
        var since = unfocusedSince;
        OnDeactivated(EventArgs.Empty);
        Check(since.HasValue && unfocusedSince == since, "Repeated deactivation preserves the start of the unfocused interval");

        closingWindow = true;
        await QueueRefreshAsync("Closing", _ => throw new Exception("Must not run"));
        Check(pendingRefresh is null && refreshTask is null, "Closing rejects new refresh requests");
        closingWindow = false;
        Console.WriteLine($"{passed} refresh checks passed.");
    }

    sealed class UnusedAdb : IAdb
    {
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
            => throw new Exception("Refresh checks must not call a real device.");
    }
}
