using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using Microsoft.Win32;
using QuestTool.Core;

namespace QuestTool;

public partial class MainWindow
{
    readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    DateTime nextMonitorCheck;
    string capturedDeviceLogs = "";
    readonly ObservableCollection<WifiNetwork> wifiNetworks = [];

    static DeploymentTarget MonitorTarget(Headset h) => new(h.Serial, h.UsbTransport.Length > 0 ? h.UsbTransport : h.Transport, h.Endpoint);

    void InitializeMonitoring()
    {
        monitorTimer.Tick += async (_, _) =>
        {
            if (!IsActive || closingWindow || refreshTask is not null || operation is not null || loadingFiles || finishingMedia || !File.Exists(settings.AdbPath)) return;
            if (MonitorTab.IsSelected && MonitorAuto.IsChecked == true && DateTime.UtcNow >= nextMonitorCheck)
                await RefreshMonitoringAsync(false);
            else if (DeviceLogsTab.IsSelected && DeviceLogAuto.IsChecked == true && DeviceLogHeadsetBox.SelectedItem is Headset)
                await RefreshDeviceLogsAsync();
        };
        monitorTimer.Start();
    }

    async Task ReadMonitoringAsync(CancellationToken token)
    {
        var service = new MonitoringService(Adb);
        foreach (var h in headsets.Where(h => h.Transport.Length > 0).ToArray())
        {
            h.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try
            {
                var snapshot = await service.ReadAsync(MonitorTarget(h), token);
                token.ThrowIfCancellationRequested();
                h.Battery = snapshot.Battery; h.Storage = snapshot.Storage; h.Network = snapshot.Wifi;
                h.MonitorStatus = "Updated";
                if (ReferenceEquals(MonitorGrid.SelectedItem, h)) h.Details = await service.ReadDetailsAsync(MonitorTarget(h), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                h.MonitorStatus = "Check failed";
                h.Details = [new("Status", "Details unavailable. " + ex.Message)];
                Log($"Monitoring {h.Name}: {ex.Message}");
            }
        }
        nextMonitorCheck = DateTime.UtcNow.AddSeconds(15);
    }

    async void MonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorGrid.SelectedItem is not Headset h || !IsLoaded) return;
        await QueueRefreshAsync("Reading headset details…", async token =>
        {
            try { h.Details = await new MonitoringService(Adb).ReadDetailsAsync(MonitorTarget(h), token); Status("Headset details updated · " + h.Name); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { h.Details = [new("Status", ex.Message)]; throw; }
        });
    }

    async void WifiHeadsetChanged(object sender, SelectionChangedEventArgs e)
    {
        wifiNetworks.Clear();
        if (CurrentWifiText is null) return;
        CurrentWifiText.Text = WifiHeadsetBox.SelectedItem is Headset h ? h.Name + " · " + h.Network : "Choose a headset.";
        if (IsLoaded && !selectingRefreshTargets && WifiHeadsetBox.SelectedItem is Headset) await ScanWifiAsync();
    }

    async void ScanWifiClick(object sender, RoutedEventArgs e) => await ScanWifiAsync();
    async Task ScanWifiAsync()
    {
        if (WifiHeadsetBox.SelectedItem is not Headset) { Status("Choose a headset to scan nearby Wi-Fi."); return; }
        await QueueRefreshAsync("Scanning headset Wi-Fi…", ReadWifiAsync);
    }

    async Task ReadWifiAsync(CancellationToken token)
    {
        if (WifiHeadsetBox.SelectedItem is not Headset h) return;
        try
        {
            var scan = await new WifiService(Adb).ScanAsync(MonitorTarget(h), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(WifiHeadsetBox.SelectedItem, h)) return;
            wifiNetworks.Clear(); foreach (var network in scan.Networks) wifiNetworks.Add(network);
            CurrentWifiText.Text = h.Name + " · " + scan.Current.Summary;
            h.Network = scan.Current.Ssid ?? "Disconnected";
            WifiScanStatus.Text = scan.Networks.Count + " network(s). " + scan.Notice;
            WifiSsidBox.SelectedItem = scan.Networks.FirstOrDefault(n => n.Ssid == scan.Current.Ssid) ?? scan.Networks.FirstOrDefault();
            Status("Wi-Fi networks loaded from " + h.Name + ".");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { if (ReferenceEquals(WifiHeadsetBox.SelectedItem, h)) WifiScanStatus.Text = ex.Message; throw; }
    }

    void WifiNetworkChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WifiSsidBox.SelectedItem is not WifiNetwork network || WifiSecurityBox is null) return;
        WifiSecurityBox.SelectedIndex = network.Security switch { "wpa3" => 1, "open" => 2, _ => 0 };
        WifiScanStatus.Text = network.Description + $" · scan age {network.Age:0} seconds" + (network.Security is "enterprise" or "wep" or "owe" ? ". Configure this network inside the headset." : "");
    }

    async void OpenHeadsetWifiClick(object sender, RoutedEventArgs e)
    {
        if (WifiHeadsetBox.SelectedItem is not Headset h) { Status("Choose a headset first."); return; }
        await RunOperationAsync("Opening headset Wi-Fi settings…", async token =>
        {
            string transport = await new DeviceService(Adb).ResolveAsync(MonitorTarget(h), token);
            (await Adb.RunAsync(["-s", transport, "shell", "am", "start", "-a", "android.settings.WIFI_SETTINGS"], token)).RequireSuccess();
            Status("Wi-Fi settings opened inside " + h.Name + ".");
        });
    }

    Task RefreshMonitoringAsync(bool reconnect) => QueueRefreshAsync("Checking headset status…", token => RefreshDevicesAsync(token, reconnect));
    async void MonitorRefreshClick(object sender, RoutedEventArgs e) => await RefreshMonitoringAsync(true);

    void DeviceLogHeadsetChanged(object sender, SelectionChangedEventArgs e)
    {
        capturedDeviceLogs = "";
        if (DeviceLogBox is null || DeviceLogStatus is null) return;
        DeviceLogBox.Clear();
        DeviceLogStatus.Text = "Read logs to capture the selected headset's recent output.";
    }

    void ApplyDeviceLogFilter()
    {
        if (DeviceLogBox is null || DeviceLogFilterBox is null) return;
        string filter = DeviceLogFilterBox.Text;
        DeviceLogBox.Text = filter.Length == 0 ? capturedDeviceLogs : string.Join('\n', capturedDeviceLogs.Split('\n').Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    void DeviceLogFilterChanged(object sender, TextChangedEventArgs e) => ApplyDeviceLogFilter();
    async void DeviceLogRefreshClick(object sender, RoutedEventArgs e) => await RefreshDeviceLogsAsync();

    async Task RefreshDeviceLogsAsync()
    {
        if (DeviceLogHeadsetBox.SelectedItem is not Headset) { Status("Choose a headset in Device logs first."); return; }
        await QueueRefreshAsync("Reading device logs…", ReadDeviceLogsAsync);
    }

    async Task ReadDeviceLogsAsync(CancellationToken token)
    {
        if (DeviceLogHeadsetBox.SelectedItem is not Headset h) return;
        char priority = ((string)((ComboBoxItem)DeviceLogPriorityBox.SelectedItem).Tag)[0];
        try
        {
            string logs = await new MonitoringService(Adb).ReadLogsAsync(MonitorTarget(h) with { Endpoint = h.Endpoint }, priority, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(DeviceLogHeadsetBox.SelectedItem, h) || priority != ((string)((ComboBoxItem)DeviceLogPriorityBox.SelectedItem).Tag)[0]) return;
            capturedDeviceLogs = logs;
            ApplyDeviceLogFilter();
            DeviceLogStatus.Text = $"{h.Name} · {DateTime.Now:HH:mm:ss} · minimum priority {priority} · up to 1,000 recent lines. Save exports the displayed text.";
            Status("Device logs loaded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { if (ReferenceEquals(DeviceLogHeadsetBox.SelectedItem, h)) DeviceLogStatus.Text = "Log read failed. Previous capture retained. " + ex.Message; throw; }
    }

    void SaveDeviceLogClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Text log|*.txt", FileName = $"QuestTool-device-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, DeviceLogBox.Text); Status("Device logs saved."); }
        catch (Exception ex) { Status(ex.Message); }
    }

    void WifiSecurityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WifiPasswordBox is null) return;
        WifiPasswordBox.IsEnabled = (string)((ComboBoxItem)WifiSecurityBox.SelectedItem).Tag != "open";
        if (!WifiPasswordBox.IsEnabled) WifiPasswordBox.Clear();
    }

    async void ChangeWifiClick(object sender, RoutedEventArgs e)
    {
        string ssid = WifiSsidBox.SelectedItem is WifiNetwork network ? network.Ssid : WifiSsidBox.Text;
        string security = (string)((ComboBoxItem)WifiSecurityBox.SelectedItem).Tag;
        string password = WifiPasswordBox.Password;
        try
        {
            if (WifiSsidBox.SelectedItem is WifiNetwork { Security: "enterprise" or "wep" or "owe" })
                throw new InvalidOperationException("This network requires setup inside the headset. Click Open headset Wi-Fi settings.");
            MonitoringService.ValidateWifi(ssid, security, password);
            var selected = headsets.Where(h => h.Selected).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Select at least one headset in Headsets first.");
            if (selected.Any(h => h.UsbTransport.Length == 0)) throw new InvalidOperationException("Every selected headset must be connected by USB. Connect USB and Refresh first.");
            await RunOperationAsync("Changing headset Wi-Fi…", async token =>
            {
                var service = new MonitoringService(Adb);
                int changed = 0;
                foreach (var h in selected)
                {
                    try
                    {
                        h.WifiStatus = "Changing network…";
                        await service.ChangeWifiAsync(h.UsbTransport, h.Serial, ssid, security, password, token, () =>
                        {
                            // A previous wireless endpoint is no longer safe after the request is sent.
                            h.Endpoint = ""; h.Transport = h.UsbTransport; h.Connection = "USB";
                            h.Network = "—"; h.Battery = "—"; h.Storage = "—"; h.MonitorStatus = "Refresh required";
                        });
                        h.Network = ssid; h.WifiStatus = "Network connected";
                        if (settings.AutoWifi)
                        {
                            try { h.Endpoint = await new DeviceService(Adb).EnableWifiAsync(h.UsbTransport, h.Serial, token); h.WifiStatus = "Connected · verified"; }
                            catch (WifiConnectionException ex) { h.Endpoint = ex.Endpoint; h.WifiStatus = "Network connected · PC cannot reach wireless ADB"; Log(h.Name + ": " + ex.Message); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { h.WifiStatus = "Network connected · wireless ADB setup failed"; Log(h.Name + ": " + ex.Message); }
                        }
                        Log(h.Name + ": Wi-Fi network changed and verified.");
                        changed++;
                    }
                    catch (OperationCanceledException) { h.WifiStatus = "Cancelled · check network in headset"; throw; }
                    catch (Exception ex) { h.WifiStatus = "Network change not confirmed"; Log(h.Name + ": " + ex.Message); }
                }
                await RefreshDevicesAsync(token, false);
                CurrentWifiText.Text = string.Join(" · ", selected.Select(h => h.Name + ": " + h.Network + " · " + h.WifiStatus));
                Status($"Wi-Fi changed and verified on {changed}/{selected.Length} headsets. Wireless ADB status is shown per headset. See Activity log for failures.");
            });
        }
        catch (Exception ex) { Status(ex.Message); }
        finally { WifiPasswordBox.Clear(); }
    }
}
