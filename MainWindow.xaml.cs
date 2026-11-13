using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuestTool.Core;

namespace QuestTool;

public partial class MainWindow : Window
{
    readonly ObservableCollection<Headset> headsets = [];
    readonly List<string> obbs = [];
    readonly HashSet<string> retrySerials = [];
    Settings settings = new();
    ApkInfo? apk;
    CancellationTokenSource? operation;
    bool loadingFiles;
    IAdb Adb => new AdbClient(settings.AdbPath);

    public MainWindow()
    {
        InitializeComponent();
        HeadsetGrid.ItemsSource = headsets;
        DeployGrid.ItemsSource = headsets;
        FileHeadsetBox.ItemsSource = headsets;
        AppHeadsetBox.ItemsSource = headsets;
        AppsGrid.ItemsSource = installedApps;
        MediaHeadsetBox.ItemsSource = headsets;
        MonitorGrid.ItemsSource = headsets;
        DeviceLogHeadsetBox.ItemsSource = headsets;
        WifiHeadsetBox.ItemsSource = headsets;
        WifiSsidBox.ItemsSource = wifiNetworks;
        FilesGrid.ItemsSource = headsetFiles;
        try { settings = Settings.Load(); }
        catch (Exception ex) { Log("Could not load settings: " + ex.Message); }
        if (!File.Exists(settings.AdbPath)) settings.AdbPath = AdbClient.Find();
        AdbPathBox.Text = settings.AdbPath;
        if (!File.Exists(settings.MetaVrPath)) settings.MetaVrPath = MediaService.FindTool();
        MetaVrPathBox.Text = settings.MetaVrPath;
        AutoWifiBox.IsChecked = settings.AutoWifi;
        if (!File.Exists(settings.MirrorPath)) settings.MirrorPath = MirrorOptions.Find();
        MirrorPathBox.Text = settings.MirrorPath;
        settings.Mirror ??= new();
        MirrorWidthBox.Text = settings.Mirror.Width.ToString(); MirrorHeightBox.Text = settings.Mirror.Height.ToString();
        MirrorFovBox.Text = settings.Mirror.Fov.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MirrorEyeBox.SelectedIndex = settings.Mirror.Eye switch { "Left" => 0, "Both" => 2, _ => 1 };
        MirrorGuardianBox.IsChecked = settings.Mirror.Guardian; MirrorNotificationsBox.IsChecked = settings.Mirror.Notifications; MirrorGuiBox.IsChecked = settings.Mirror.SystemGui;
        StreamingBackendBox.SelectedIndex = settings.StreamingBackend == 1 ? 1 : 0;
        SubnetBox.Text = DeviceService.DefaultNetwork();
        EndpointBox.Text = "";
        foreach (var saved in settings.Headsets.DistinctBy(h => h.Serial))
            headsets.Add(new() { Serial = saved.Serial, Name = saved.Name, Endpoint = saved.Endpoint, Model = saved.Model });
        Loaded += async (_, _) =>
        {
            if (string.IsNullOrEmpty(settings.AdbPath)) { Tabs.SelectedItem = SetupTab; Status("Choose adb.exe in Setup to get started."); }
            else await RefreshCurrentTabAsync();
        };
        UpdateCount();
        UpdateMediaControls(false);
        InitializeMonitoring();
        InitializeRefresh();
    }

    void Status(string text) => StatusText.Text = text;
    void Log(string text)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        if (LogBox.Text.Length > 250000) LogBox.Text = LogBox.Text[^180000..];
        LogBox.ScrollToEnd();
    }

    void SaveSettings()
    {
        settings.Headsets = headsets.Select(h => new RememberedHeadset(h.Serial, h.Name, h.Endpoint, h.Model)).ToList();
        try { settings.Save(); }
        catch (Exception ex) { Log("Could not save settings: " + ex.Message); Status("Could not save headset settings. See Activity log."); }
    }

    void SetBusy(bool busy)
    {
        DeviceControls.IsEnabled = !busy;
        AdbControls.IsEnabled = !busy;
        FileControls.IsEnabled = !busy;
        DeployButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        WaitOffline.IsEnabled = !busy;
        HeadsetGrid.IsEnabled = !busy;
        DeployGrid.IsEnabled = !busy;
        FileTargetControls.IsEnabled = !busy;
        FilePathControls.IsEnabled = !busy;
        FileActionControls.IsEnabled = !busy;
        FilesGrid.IsEnabled = !busy;
        MonitorControls.IsEnabled = !busy;
        DeviceLogControls.IsEnabled = !busy;
        WifiControls.IsEnabled = !busy;
        AppsControls.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        FileCancelButton.IsEnabled = busy && !loadingFiles;
        UpdateMediaControls(busy);
    }

    async Task RunOperationAsync(string title, Func<CancellationToken, Task> action, bool requiresAdb = true)
    {
        if (operation is not null || loadingFiles || finishingMedia) return;
        if (requiresAdb && !File.Exists(settings.AdbPath)) { Tabs.SelectedItem = SetupTab; Status("Choose adb.exe in Setup first."); return; }
        HeadsetGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        HeadsetGrid.CommitEdit(DataGridEditingUnit.Row, true);
        operation = new();
        await StopRefreshAsync();
        SetBusy(true);
        Status(title); Log(title);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status("Operation cancelled. Completed changes are retained."); Log("Operation cancelled."); }
        catch (Exception ex) { Status(ex.Message); Log(ex.ToString()); }
        finally { operation.Dispose(); operation = null; SetBusy(false); SaveSettings(); UpdateCount(); _ = ResumeRefreshAsync(); }
    }

    void UpdateCount() => FleetCount.Text = $"{headsets.Count(h => h.Connection != "Offline")} connected / {headsets.Count} remembered";

    Task RefreshAsync(CancellationToken token) => RefreshDevicesAsync(token, true);

    async Task RefreshDevicesAsync(CancellationToken token, bool reconnect, bool readMonitoring = true)
    {
        var devices = new DeviceService(Adb);
        var notices = new List<string>();
        var initial = await devices.ListAsync(message => { notices.Add(message); Log(message); }, token);
        Headset Remember(ConnectedHeadset device)
        {
            var h = headsets.FirstOrDefault(h => h.Serial == device.Identity.Serial);
            if (h is null)
            {
                h = new() { Serial = device.Identity.Serial, Name = device.Identity.Model + " · " + device.Identity.Serial[^Math.Min(4, device.Identity.Serial.Length)..], Selected = true };
                headsets.Add(h);
            }
            return h;
        }
        if (reconnect)
        {
            if (settings.AutoWifi)
                foreach (var usb in initial.Where(d => !d.Wireless))
                {
                    var h = Remember(usb);
                    try
                    {
                        h.Endpoint = await devices.EnableWifiAsync(usb.Transport, h.Serial, token);
                        h.WifiStatus = "Connected · verified";
                    }
                    catch (WifiConnectionException ex) { h.Endpoint = ex.Endpoint; h.WifiStatus = "Enabled · PC cannot connect"; notices.Add(h.Name + ": " + ex.Message); Log(notices[^1]); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { h.WifiStatus = "Auto-connect failed"; notices.Add(h.Name + ": " + ex.Message); Log(notices[^1]); }
                }
            foreach (var h in headsets.Where(h => DeviceService.IsEndpoint(h.Endpoint) && !initial.Any(d => d.Identity.Serial == h.Serial && (d.Wireless || settings.AutoWifi))))
            {
                if (!await DeviceService.PortOpenAsync(h.Endpoint.Split(':')[0], token, int.Parse(h.Endpoint.Split(':')[1]))) { h.WifiStatus = "Wi-Fi unreachable · check headset network"; continue; }
                try { await devices.ConnectAsync(h.Endpoint, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { h.WifiStatus = "Reconnect failed"; Log($"{h.Name}: {ex.Message}"); }
            }
        }
        var found = reconnect ? await devices.ListAsync(message => { notices.Add(message); Log(message); }, token) : initial;
        token.ThrowIfCancellationRequested();
        foreach (var h in headsets)
        {
            h.Connection = "Offline"; h.Transport = ""; h.UsbTransport = "";
            if (!found.Any(d => d.Identity.Serial == h.Serial))
            {
                h.Battery = "—"; h.Storage = "—"; h.Network = "—"; h.MonitorStatus = "Offline";
            }
            if (h.WifiStatus == "Connected · verified") h.WifiStatus = "Not connected";
        }
        foreach (var device in found.OrderBy(d => d.Wireless))
        {
            var h = Remember(device);
            h.Model = device.Identity.Model;
            if (device.Wireless) { h.Endpoint = device.Transport; h.Transport = h.UsbTransport.Length > 0 ? h.UsbTransport : device.Transport; h.Connection = h.UsbTransport.Length > 0 ? "Wi-Fi + USB" : "Wi-Fi"; h.WifiStatus = "Connected · verified"; }
            else { h.UsbTransport = device.Transport; h.Transport = device.Transport; h.Connection = "USB"; }
        }
        if (readMonitoring) await ReadMonitoringAsync(token);
        if (readMonitoring && apk is not null && reconnect) await ReadVersionsAsync(token);
        UpdateCount();
        Status(notices.Count > 0 ? $"Refreshed. {notices[0]} See Activity log." : $"Ready · {headsets.Count(h => h.Connection != "Offline")} headset(s) connected.");
    }

    void AutoWifiChanged(object sender, RoutedEventArgs e)
    {
        settings.AutoWifi = AutoWifiBox.IsChecked == true;
        SaveSettings();
    }

    async Task ReadVersionsAsync(CancellationToken token)
    {
        var package = apk?.Package;
        if (package is null) return;
        foreach (var h in headsets.ToArray())
        {
            if (h.Transport.Length == 0) { h.Version = "—"; continue; }
            try
            {
                var result = await Adb.RunAsync(["-s", h.Transport, "shell", "dumpsys", "package", package], token, 10);
                token.ThrowIfCancellationRequested();
                if (apk?.Package != package) return;
                h.Version = DeploymentService.ParseVersion(result.RequireSuccess())?.ToString() ?? "Not installed";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log($"Version check for {h.Name}: {ex.Message}"); }
        }
    }

    async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(settings.AdbPath)) { Tabs.SelectedItem = SetupTab; Status("Choose adb.exe in Setup first."); return; }
        await RefreshCurrentTabAsync();
    }
    async void EnableWifiClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Enabling wireless ADB…", async token =>
    {
        var selected = headsets.Where(h => h.Selected && h.UsbTransport.Length > 0).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Tick a headset connected by USB first. Refresh after plugging it in.");
        var service = new DeviceService(Adb);
        var failures = new List<string>();
        foreach (var h in selected)
        {
            try
            {
                h.Endpoint = await service.EnableWifiAsync(h.UsbTransport, h.Serial, token, message => { h.WifiStatus = message; Status(h.Name + ": " + message); Log(h.Name + ": " + message); });
                h.WifiStatus = "Connected · verified";
                Log($"{h.Name}: Wi-Fi enabled at {h.Endpoint}. USB can be unplugged.");
            }
            catch (WifiConnectionException ex)
            {
                h.Endpoint = ex.Endpoint;
                h.WifiStatus = "Enabled · PC cannot connect";
                failures.Add(h.Name + ": " + ex.Message);
                Log(failures[^1]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                h.WifiStatus = "Setup failed";
                failures.Add(h.Name + ": " + ex.Message);
                Log(failures[^1]);
            }
        }
        await RefreshAsync(token);
        Status(failures.Count == 0 ? "Wi-Fi confirmed. You can unplug USB from the selected headsets." : failures[0]);
    });

    async void ConnectClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Connecting…", async token =>
    {
        var service = new DeviceService(Adb);
        await service.ConnectAsync(EndpointBox.Text.Trim(), token);
        await RefreshAsync(token);
    });

    async void ScanClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Scanning network for wireless ADB…", async token =>
    {
        string[] hosts = DeviceService.NetworkHosts(SubnetBox.Text).ToArray();
        var endpoints = new ConcurrentBag<string>();
        int checkedCount = 0;
        var progress = new Progress<int>(count => Status($"Scanning ADB port 5555 · {count}/{hosts.Length} addresses"));
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token }, async (host, ct) =>
        {
            if (await DeviceService.PortOpenAsync(host, ct)) endpoints.Add(host + ":5555");
            ((IProgress<int>)progress).Report(Interlocked.Increment(ref checkedCount));
        });
        var service = new DeviceService(Adb);
        foreach (string endpoint in endpoints)
        {
            try { await service.ConnectAsync(endpoint, token); Log("ADB endpoint found: " + endpoint); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log(endpoint + ": " + ex.Message); }
        }
        await RefreshAsync(token);
        Status($"Scan finished · {endpoints.Count} ADB endpoint(s) found. Only verified Quest headsets are listed.");
    });

    void SelectAllClick(object sender, RoutedEventArgs e) { foreach (var h in headsets) h.Selected = true; }
    void SelectNoneClick(object sender, RoutedEventArgs e) { foreach (var h in headsets) h.Selected = false; }
    void NameEdited(object? sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(new Action(SaveSettings));
    async void ForgetClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Forgetting selected headsets…", _ =>
    {
        pendingRefresh = null;
        foreach (var h in headsets.Where(h => h.Selected).ToArray()) headsets.Remove(h);
        Status("Selected headsets forgotten. Connected headsets can be found again with Refresh.");
        return Task.CompletedTask;
    }, requiresAdb: false);

    void ChooseAdbClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "ADB executable|adb.exe", Title = "Select Android Debug Bridge" };
        if (picker.ShowDialog(this) != true) return;
        settings.AdbPath = picker.FileName;
        media = null;
        AdbPathBox.Text = settings.AdbPath;
        SaveSettings(); Status("ADB configured. Open Headsets and Refresh.");
    }

    async void ChooseApkClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Android APK|*.apk" };
        if (picker.ShowDialog(this) == true) await LoadFilesAsync([picker.FileName]);
    }
    async void ChooseObbClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Expansion files|*.obb", Multiselect = true };
        if (picker.ShowDialog(this) == true) await LoadFilesAsync(picker.FileNames);
    }
    void FilesDragOver(object sender, DragEventArgs e) { e.Effects = operation is null && !loadingFiles && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void FilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await LoadFilesAsync(files);
    }
    async Task LoadFilesAsync(string[] files)
    {
        if (operation is not null || loadingFiles) return;
        loadingFiles = true;
        await StopRefreshAsync();
        SetBusy(true); CancelButton.IsEnabled = false;
        try
        {
            if (files.Any(f => !File.Exists(f) || Path.GetExtension(f).ToLowerInvariant() is not (".apk" or ".obb"))) throw new InvalidDataException("Choose APK and OBB files only.");
            var apks = files.Where(f => Path.GetExtension(f).Equals(".apk", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (apks.Length > 1) throw new InvalidDataException("Choose one standalone APK per deployment.");
            var candidate = apks.Length == 1 ? await Task.Run(() => ApkInfo.Read(apks[0])) : apk;
            if (candidate is null) throw new InvalidDataException("Choose an APK first, or drop it together with the OBB files.");
            var expansion = (apks.Length == 1 ? Enumerable.Empty<string>() : obbs).Concat(files.Where(f => Path.GetExtension(f).Equals(".obb", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            candidate.ValidateObbs(expansion);
            apk = candidate; obbs.Clear(); obbs.AddRange(expansion); retrySerials.Clear();
            foreach (var h in headsets) { h.Version = "—"; h.Result = ""; }
            PackageSummary.Text = $"{apk.Package}  ·  {apk.VersionName}  ·  version code {apk.VersionCode}";
            FileSummary.Text = "APK: " + Path.GetFileName(apk.Path) + (obbs.Count == 0 ? "\nNo OBB files selected." : "\nOBB destination: " + apk.ObbDirectory + "/\n" + string.Join("\n", obbs.Select(Path.GetFileName)) + "\nOriginal filenames retained · file size + SHA-256 checked on the headset");
            FileSummary.ToolTip = string.Join("\n", new[] { apk.Path }.Concat(obbs));
            Log("Prepared " + PackageSummary.Text);
            await ReadVersionsAsync(CancellationToken.None);
            Status("Files ready. Select your target headsets, then Deploy.");
        }
        catch (Exception ex) { Status(ex.Message); Log(ex.Message); }
        finally { loadingFiles = false; SetBusy(false); _ = ResumeRefreshAsync(); }
    }
    void ClearFilesClick(object sender, RoutedEventArgs e)
    {
        apk = null; obbs.Clear(); retrySerials.Clear(); PackageSummary.Text = "No APK selected"; FileSummary.Text = "";
        foreach (var h in headsets) { h.Version = "—"; h.Result = ""; }
    }

    async void DeployClick(object sender, RoutedEventArgs e) => await StartDeploymentAsync(false);
    async void RetryClick(object sender, RoutedEventArgs e) => await StartDeploymentAsync(true);
    async Task StartDeploymentAsync(bool retry)
    {
        await RunOperationAsync("Preparing deployment…", async token =>
        {
            if (apk is null) throw new InvalidOperationException("Choose an APK in Deploy first.");
            var targets = headsets.Where(h => h.Selected && (!retry || retrySerials.Contains(h.Serial))).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException(retry ? "No selected failed/cancelled headsets to retry." : "Select at least one headset.");
            // Hold file handles to prevent artifacts being replaced or edited while jobs are pending.
            var handles = new List<FileStream>();
            try
            {
                foreach (var file in new[] { apk.Path }.Concat(obbs)) handles.Add(new(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                var fresh = await Task.Run(() => ApkInfo.Read(apk.Path), token);
                if (fresh != apk) throw new IOException("APK changed since selection. Choose the APK again.");
                fresh.ValidateObbs(obbs);
                bool wait = WaitOffline.IsChecked == true;
                int complete = 0;
                foreach (var h in targets) { h.Result = "Queued"; h.Active = true; retrySerials.Add(h.Serial); }
                var progress = new Progress<DeploymentUpdate>(update =>
                {
                    var h = targets.Single(h => h.Serial == update.Serial);
                    h.Result = update.Message; h.Active = update.Active;
                    if (update.Version is not null) h.Version = update.Version;
                    Log($"{h.Name}: {update.Message}");
                    if (!update.Active)
                    {
                        if (update.Message == "Updated · verified") retrySerials.Remove(h.Serial);
                        complete++;
                    }
                    Status($"Deployment · {complete}/{targets.Length} finished. See results for each headset.");
                });
                var service = new DeploymentService(Adb);
                await Task.WhenAll(targets.Select(h => service.DeployAsync(new(h.Serial, h.Transport, h.Endpoint), fresh, obbs.ToArray(), wait, progress, token)));
                // Drain progress callbacks queued on this dispatcher before computing final counts.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                int succeeded = targets.Count(h => h.Result == "Updated · verified");
                Status($"Deployment finished · {succeeded}/{targets.Length} verified. {targets.Length - succeeded} need attention.");
                Log(StatusText.Text);
            }
            finally { foreach (var handle in handles) handle.Dispose(); foreach (var h in targets) h.Active = false; }
        });
    }

    void CancelClick(object sender, RoutedEventArgs e) { pendingRefresh = null; operation?.Cancel(); refreshCancellation?.Cancel(); Status("Cancelling… Completed changes will be retained."); }
    void OpenLinkClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo((string)((Button)sender).Tag) { UseShellExecute = true }); }
        catch (Exception ex) { Status(ex.Message); }
    }
    void SaveLogClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Text log|*.txt", FileName = $"QuestTool-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, LogBox.Text); Status("Activity log saved."); }
        catch (Exception ex) { Status(ex.Message); }
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingWindow) { e.Cancel = true; return; }
        if (finishingMedia || stoppingMirror) { e.Cancel = true; return; }
        if (operation is not null || loadingFiles)
        {
            e.Cancel = true;
            Status("An operation is running. Click Cancel operation and wait for it to finish before closing.");
            return;
        }
        if (refreshTask is not null)
        {
            e.Cancel = true;
            closingWindow = true;
            pendingRefresh = null;
            await StopRefreshAsync();
            closingWindow = false;
            Close();
            return;
        }
        if (HasMediaState)
        {
            e.Cancel = true;
            finishingMedia = true;
            SetBusy(true);
            Status("Saving recordings, closing live view, and restoring proximity sensors…");
            bool finished;
            try { finished = await FinishMediaAsync(); }
            finally { finishingMedia = false; SetBusy(false); }
            if (finished) Close();
            return;
        }
        SaveSettings();
        monitorTimer.Stop();
    }
}
