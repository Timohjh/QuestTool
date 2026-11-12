using QuestTool.Core;

static class NetworkDetailsChecks
{
    // Synthetic network fixtures: documentation IP ranges and locally administered MACs.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var status = WifiService.ParseStatus("Wifi is connected to \"Guest\"\nWifiInfo: SSID: \"A network's name\", BSSID: 00:11:22:33:44:55, IP: /192.0.2.3, RSSI: -44, Link speed: 2401Mbps, Frequency: 5520MHz");
        check(status.Ssid == "A network's name" && status.IpAddress == "192.0.2.3" && status.Signal == "-44 dBm" && status.Link == "2401Mbps", "Read real Quest Wi-Fi status format including current network, address, signal and link");
        check(WifiService.ParseStatus("Wifi is disabled").Ssid is null, "Disconnected Wi-Fi never shows a stale SSID");
        var networks = WifiService.ParseNetworks("""
             BSSID Frequency RSSI Age(sec) SSID Flags
             02:00:00:00:00:01 5300 -68(0:-68/1:-128) 12.123 A network's name   [WPA2-PSK-CCMP][ESS]
             02:00:00:00:00:02 5520 -42(0:-128/1:-128) 8.222 A network's name [WPA2-PSK-CCMP][ESS]
             02:00:00:00:00:03 5520 -46 2.5 Guest network [WPA2-PSK-CCMP][ESS]
             02:00:00:00:00:04 5180 -90 3 Enterprise network [WPA2-EAP/SHA1-CCMP][ESS]
             02:00:00:00:00:05 2412 -79 4 Public [ESS]
             02:00:00:00:00:06 5955 -50 5 Secure [RSN-SAE-CCMP][ESS]
             02:00:00:00:00:07 5180 -90 3       [WPA2-PSK-CCMP][ESS]
             02:00:00:00:00:08 5180 100 3 Invalid signal [ESS]
            """);
        check(networks.Count == 5 && networks[0].Ssid == "A network's name" && networks[0].Signal == -42, "Parse multi-chain scan signals, deduplicate access points, sort strongest first and omit hidden/invalid entries");
        check(networks.Single(n => n.Ssid == "Enterprise network").Security == "enterprise" && networks.Single(n => n.Ssid == "Public").Security == "open" && networks.Single(n => n.Ssid == "Secure").Security == "wpa3", "Identify supported and restricted network security types");
        check(networks.Single(n => n.Ssid == "Secure").Description.Contains("6 GHz"), "Show the detected Wi-Fi frequency band");
        var data = DeviceDetails.Parse("""
            __SYSTEM__
            manufacturer=Qualcomm
            soc=SXR2230P
            android=14
            build=build123
            kernel=5.10
            __MEMORY__
            MemTotal: 8388608 kB
            MemAvailable: 3145728 kB
            __CPU_A__
            cpu 100 0 50 800 50 0 0 0 999 999
            __CPU_B__
            cpu 120 0 60 850 70 0 0 0 999 999
            __CPU_CLOCK__
            /sys/devices/system/cpu/cpufreq/policy0/scaling_cur_freq 1920000
            /sys/devices/system/cpu/cpufreq/policy2/scaling_cur_freq 1651200
            __GPU__
            gpu_model=Adreno740v3
            gpuclk=492000000
            gpubusy=250 1000
            __THERMAL__
            Thermal Status: 0
            Temperature{mValue=41.2, mType=0, mName=cpu0, mStatus=0}
            Temperature{mValue=43.4, mType=0, mName=cpu1, mStatus=0}
            Temperature{mValue=40.1, mType=1, mName=gpu, mStatus=0}
            __WIFI__
            SSID: "Lab", BSSID: 00:11:22:33:44:55, IP: /198.51.100.2, RSSI: -44, Link speed: 2401Mbps
            __BATTERY__
            temperature: 250
            """);
        string Value(string name) => data.Single(m => m.Name == name).Value;
        check(Value("Chipset") == "Qualcomm SXR2230P" && Value("Android") == "14" && Value("OS build") == "build123", "Read chipset and OS properties without mixing missing fields");
        check(Value("CPU usage · 250 ms sample").StartsWith("30") && Value("CPU cluster clocks").Contains("1920 MHz"), "Calculate sampled CPU activity excluding idle/iowait and convert cluster clocks");
        check(Value("GPU busy · driver sample").StartsWith("25") && Value("GPU clock") == "492 MHz" && Value("GPU") == "Adreno740v3", "Read GPU model, actual clock and driver activity");
        check(Value("RAM total").StartsWith("8") && Value("RAM used / available").StartsWith("5"), "Use available memory rather than only free pages for RAM usage");
        check(Value("CPU temperature · highest sensor").StartsWith("43") && Value("Battery temperature").StartsWith("25") && Value("Thermal status") == "Normal", "Read actual sensor temperatures and thermal state");
        check(DeviceDetails.Parse("permission denied").All(m => m.Value.Contains("unavailable", StringComparison.OrdinalIgnoreCase)), "Restricted hardware metrics display unavailable rather than fabricated zeroes");
        var mirror = new MirrorOptions { Width = 1280, Height = 720, Eye = "Both", Fov = 1.3, Guardian = false, Notifications = true, SystemGui = false };
        var arguments = mirror.StartInfo(Path.Combine(Path.GetTempPath(), "QuestTool-tests", "OculusMirror.exe")).ArgumentList;
        check(arguments.Contains("--RectilinearBothEyes") && arguments.Contains("1.3") && arguments.Contains("--IncludeNotifications") && !arguments.Contains("--IncludeGuardian") && !arguments.Contains("--IncludeSystemGui"), "Build documented OculusMirror settings using separate process arguments");
        bool rejected = false;
        try { new MirrorOptions { Fov = double.NaN }.StartInfo("OculusMirror.exe"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Reject invalid mirror settings before process launch");
        var fake = new HiddenJoinAdb();
        await new MonitoringService(fake).ChangeWifiAsync("USB", "SERIAL", "Lab", "open", "", default);
        check(fake.Probed && fake.Joined, "Join supported Wi-Fi even when firmware omits connect-network from help");
    }

    sealed class HiddenJoinAdb : IAdb
    {
        public bool Probed, Joined;
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            string command = arguments.Last();
            string output = "";
            if (command == DeviceService.IdentityCommand) output = "Meta\nQuest 3\nSERIAL";
            if (command == "cmd wifi help") output = "status list-scan-results";
            if (command == "cmd wifi connect-network") { output = "Invalid args for connect-network: Argument expected"; Probed = true; }
            if (command.StartsWith("cmd wifi connect-network ")) Joined = true;
            if (command == "cmd wifi status") output = "SSID: \"Lab\", BSSID: 00:11:22:33:44:55, Supplicant state: COMPLETED";
            return Task.FromResult(new CommandResult(0, output));
        }
    }
}
