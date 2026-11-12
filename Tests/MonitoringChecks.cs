using QuestTool.Core;

static class MonitoringChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var snapshot = MonitoringService.Parse("Current Battery Service state:\r\n  level: 75\r\n  scale: 100\r\n  status: 2\r\n__QUESTTOOL_STORAGE__\nFilesystem 1K-blocks Used Available Use% Mounted on\n/dev/data 10485760 5242880 5242880 50% /data\n__QUESTTOOL_WIFI__\nWifi is enabled\nWifi is connected to SSID: \"Lab Wi-Fi\", BSSID: 00:11:22:33:44:55, Supplicant state: COMPLETED");
        check(snapshot.Battery == "75% · Charging" && snapshot.Storage.Contains("5") && snapshot.Wifi == "Lab Wi-Fi", "Parse monitoring metrics with CRLF battery dump and storage");
        var missing = MonitoringService.Parse("permission denied\n__QUESTTOOL_STORAGE__\ninvalid\n__QUESTTOOL_WIFI__\nWifi is disabled");
        check(missing.Battery == "Unavailable" && missing.Storage == "Unavailable" && missing.Wifi == "Disabled", "Missing metrics never appear as zero battery or storage");
        bool rejected = false;
        try { MonitoringService.Parse("partial output"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Reject incomplete monitoring response");

        var fake = new MonitoringAdb();
        var service = new MonitoringService(fake);
        var target = new DeploymentTarget("SERIAL", "USB", "");
        string logs = await service.ReadLogsAsync(target, 'W', default);
        check(logs == "device log" && fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "logcat", "-d", "-t", "1000", "-v", "threadtime", "*:W" }), "Read bounded log snapshot on verified device with priority filter");
        fake.Serial = "OTHER";
        rejected = false;
        try { await service.ReadLogsAsync(target, 'I', default); } catch (IOException) { rejected = true; }
        check(rejected && fake.Commands.Count(c => c.Contains("logcat")) == 1, "Reject logs from a mismatched headset");
        fake.Serial = "SERIAL";
        await service.ChangeWifiAsync("USB", "SERIAL", "Lab's $(echo nope)", "wpa2", "secret'$(x)", default);
        string command = fake.Commands.Single(c => c.Last().StartsWith("cmd wifi connect-network"))[3];
        check(command.Contains("'Lab'\"'\"'s $(echo nope)'") && command.Contains("'secret'\"'\"'$(x)'"), "Shell-quote SSID and password against command injection");
        check(fake.Commands.Last().Last() == "cmd wifi status", "Verify actual Wi-Fi association after request");

        int before = fake.Commands.Count;
        rejected = false;
        try { await service.ChangeWifiAsync("198.51.100.2:5555", "SERIAL", "Lab", "open", "", default); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && fake.Commands.Count == before, "Require USB before changing network");
        fake.Serial = "OTHER";
        rejected = false;
        try { await service.ChangeWifiAsync("USB", "SERIAL", "Lab", "open", "", default); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && fake.Commands.Count(c => c.Last().StartsWith("cmd wifi connect-network")) == 1, "Reject network change on wrong serial");
        fake.Serial = "SERIAL"; fake.DenyChange = true;
        rejected = false;
        try { await service.ChangeWifiAsync("USB", "SERIAL", "Lab", "wpa2", "secret123", default); }
        catch (InvalidOperationException ex) { rejected = !ex.ToString().Contains("secret123"); }
        check(rejected, "Wi-Fi permission failures do not expose password in exception");
        rejected = false;
        try { MonitoringService.ValidateWifi("Lab", "wpa2", "short"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Reject invalid WPA2 credentials before ADB");
        fake.DenyChange = false; fake.SupportsChange = false;
        bool changing = false;
        rejected = false;
        try { await service.ChangeWifiAsync("USB", "SERIAL", "Lab", "open", "", default, () => changing = true); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && !changing, "Unsupported Wi-Fi command leaves existing endpoint untouched");
        fake.SupportsChange = true;
        await service.ChangeWifiAsync("USB", "SERIAL", "Lab's $(echo nope)", "open", "", default, () => changing = true);
        check(changing && fake.Commands.Last(c => c.Last().StartsWith("cmd wifi connect-network")).Last().EndsWith(" open"), "Open network sends no password and invalidates old endpoint when issuing request");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        rejected = false;
        try { await service.ReadLogsAsync(target, 'I', cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
        check(rejected, "Cancel device log read");
    }

    sealed class MonitoringAdb : IAdb
    {
        public string Serial = "SERIAL";
        public bool DenyChange, SupportsChange = true;
        public List<string[]> Commands = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = arguments.ToArray(); Commands.Add(a);
            string command = a.Last();
            if (command == DeviceService.IdentityCommand) return Task.FromResult(new CommandResult(0, "Meta\nQuest 3\n" + Serial));
            if (command == "cmd wifi help") return Task.FromResult(new CommandResult(0, SupportsChange ? "connect-network <ssid> open|wpa2|wpa3" : "status"));
            if (command.StartsWith("cmd wifi connect-network") && DenyChange) return Task.FromResult(new CommandResult(0, "SecurityException: denied password secret123"));
            if (command == "cmd wifi status") return Task.FromResult(new CommandResult(0, "Wifi is connected to SSID: \"Lab's $(echo nope)\", BSSID: 00:11:22:33:44:55, Supplicant state: COMPLETED"));
            return Task.FromResult(new CommandResult(0, a.Contains("logcat") ? "device log" : ""));
        }
    }
}
