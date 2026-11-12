using System.Text;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed record DeviceSnapshot(string Battery, string Storage, string Wifi);

public sealed class MonitoringService(IAdb adb)
{
    internal const string SnapshotCommand = "dumpsys battery; echo __QUESTTOOL_STORAGE__; df -k /data; echo __QUESTTOOL_WIFI__; cmd wifi status || true";

    public async Task<DeviceSnapshot> ReadAsync(DeploymentTarget target, CancellationToken token)
    {
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        string output = (await adb.RunAsync(["-s", transport, "shell", SnapshotCommand], token, 10)).RequireSuccess();
        return Parse(output);
    }

    public async Task<IReadOnlyList<DeviceMetric>> ReadDetailsAsync(DeploymentTarget target, CancellationToken token)
    {
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        return DeviceDetails.Parse((await adb.RunAsync(["-s", transport, "shell", DeviceDetails.Command], token, 15)).RequireSuccess());
    }

    public static DeviceSnapshot Parse(string output)
    {
        string[] sections = output.Split(["__QUESTTOOL_STORAGE__", "__QUESTTOOL_WIFI__"], StringSplitOptions.None);
        if (sections.Length != 3) throw new InvalidDataException("Incomplete monitoring response. Refresh to retry.");
        int? Number(string key) => int.TryParse(Regex.Match(sections[0], @"(?m)^\s*" + key + @":\s*(\d+)\s*$").Groups[1].Value, out int value) ? value : null;
        int? level = Number("level"), scale = Number("scale"), status = Number("status");
        string battery = level is >= 0 && scale is > 0 && level <= scale
            ? $"{level * 100d / scale:0}%" + (status == 2 ? " · Charging" : status == 5 ? " · Full" : "") : "Unavailable";
        string storage = "Unavailable";
        foreach (string line in sections[1].Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 6 && fields[^1] == "/data" && long.TryParse(fields[1], out long total) && long.TryParse(fields[3], out long free) && total > 0 && free >= 0 && free <= total)
                storage = $"{free / 1048576d:0.0} / {total / 1048576d:0.0} GB free";
        }
        string wifi = ParseSsid(sections[2]) ?? (sections[2].Contains("Wifi is disabled", StringComparison.OrdinalIgnoreCase) ? "Disabled" : "Unavailable / disconnected");
        return new(battery, storage, wifi);
    }

    public static string? ParseSsid(string output)
    {
        var match = Regex.Match(output, "SSID: (?:\"(?<ssid>.*?)\"|(?<ssid>.*?)), BSSID:");
        return match.Success && match.Groups["ssid"].Value != "<unknown ssid>" ? match.Groups["ssid"].Value : null;
    }

    public async Task<string> ReadLogsAsync(DeploymentTarget target, char priority, CancellationToken token)
    {
        if (!"VDIWEF".Contains(priority)) throw new ArgumentException("Choose a valid log priority.");
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        return (await adb.RunAsync(["-s", transport, "logcat", "-d", "-t", "1000", "-v", "threadtime", "*:" + priority], token, 10)).RequireSuccess();
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static void ValidateWifi(string ssid, string security, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid) || Encoding.UTF8.GetByteCount(ssid) > 32 || ssid.Any(char.IsControl))
            throw new ArgumentException("Enter a Wi-Fi name of 1–32 UTF-8 bytes without control characters.");
        if (security is not ("open" or "wpa2" or "wpa3")) throw new ArgumentException("Choose Open, WPA2 Personal, or WPA3 Personal.");
        bool hexKey = password.Length == 64 && password.All(Uri.IsHexDigit);
        if (security == "wpa2" && !(password.Length is >= 8 and <= 63 || hexKey) || security == "wpa3" && password.Length is not (>= 1 and <= 63) || password.Any(char.IsControl))
            throw new ArgumentException("Check the Wi-Fi password length (WPA2: 8–63 characters or 64 hex digits; WPA3: 1–63 characters).");
    }

    public async Task ChangeWifiAsync(string usbTransport, string serial, string ssid, string security, string password, CancellationToken token, Action? changing = null)
    {
        ValidateWifi(ssid, security, password);
        if (string.IsNullOrWhiteSpace(usbTransport) || DeviceService.IsEndpoint(usbTransport)) throw new InvalidOperationException("Connect the headset by USB and Refresh before changing Wi-Fi.");
        var devices = new DeviceService(adb);
        if ((await devices.IdentifyAsync(usbTransport, token)).Serial != serial) throw new InvalidOperationException("USB headset identity changed. Refresh first.");
        string help = (await adb.RunAsync(["-s", usbTransport, "shell", "cmd wifi help"], token, 8)).RequireSuccess();
        if (!help.Contains("connect-network <ssid>", StringComparison.Ordinal))
        {
            // Some Horizon OS versions omit this working command from help.
            // Calling it without arguments validates availability without changing a network.
            var probe = await adb.RunAsync(["-s", usbTransport, "shell", "cmd wifi connect-network"], token, 8);
            if (!probe.Output.Contains("Invalid args for connect-network", StringComparison.Ordinal))
                throw new InvalidOperationException("This headset does not expose Wi-Fi joining over ADB. Change Wi-Fi inside the headset.");
        }
        string command = "cmd wifi connect-network " + Quote(ssid) + " " + security + (security == "open" ? "" : " " + Quote(password));
        token.ThrowIfCancellationRequested();
        changing?.Invoke();
        // Never surface command output or exceptions here: firmware may echo credentials.
        try
        {
            var result = await adb.RunAsync(["-s", usbTransport, "shell", command], token, 15);
            if (result.ExitCode != 0 || result.Output.Contains("exception", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("failed", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("denied", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("Error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Wi-Fi change was not confirmed. Check headset permissions and credentials, or change Wi-Fi inside the headset."); }
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string status = (await adb.RunAsync(["-s", usbTransport, "shell", "cmd wifi status"], token, 8)).RequireSuccess();
            if (ParseSsid(status) == ssid && status.Contains("COMPLETED", StringComparison.Ordinal)) return;
            if (attempt < 7) await Task.Delay(1500, token);
        }
        throw new InvalidOperationException("Wi-Fi request sent, but connection to the requested network was not confirmed. Check the headset; keep USB connected.");
    }
}
