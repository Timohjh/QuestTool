using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed record WifiStatus(string? Ssid, string IpAddress, string Signal, string Link)
{
    public string Summary => Ssid is null ? "Not connected / unavailable" : $"{Ssid} · {IpAddress} · {Signal} · {Link}";
}
public sealed record WifiNetwork(string Ssid, string Security, int Signal, int Frequency, double Age)
{
    public string Description => $"{Ssid}  ·  {Security.ToUpperInvariant()}  ·  {Signal} dBm  ·  {(Frequency >= 5925 ? "6" : Frequency >= 4900 ? "5" : "2.4")} GHz";
}
public sealed record WifiScan(WifiStatus Current, IReadOnlyList<WifiNetwork> Networks, string Notice);

public sealed class WifiService(IAdb adb)
{
    public async Task<WifiScan> ScanAsync(DeploymentTarget target, CancellationToken token)
    {
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        async Task<CommandResult> Shell(string command) => await adb.RunAsync(["-s", transport, "shell", command], token, 10);
        var scan = await Shell("cmd wifi start-scan");
        bool fresh = scan.ExitCode == 0 && !Regex.IsMatch(scan.Output, "exception|denied|failed|error", RegexOptions.IgnoreCase);
        if (fresh) await Task.Delay(1500, token);
        string listing = (await Shell("cmd wifi list-scan-results")).RequireSuccess();
        if (Regex.IsMatch(listing, "exception|permission denied", RegexOptions.IgnoreCase)) throw new IOException("The headset denied Wi-Fi scan access. Open Wi-Fi settings inside the headset.");
        string status = (await Shell("cmd wifi status")).RequireSuccess();
        return new(ParseStatus(status), ParseNetworks(listing), fresh ? "Latest headset scan. Results include their reported age." : "Scan request was restricted; showing the headset's cached results.");
    }

    public static WifiStatus ParseStatus(string text)
    {
        string Match(string pattern) => Regex.Match(text, pattern).Groups[1].Value;
        string ip = Match(@"\bIP:\s*/?([\d.]+)");
        string signal = Match(@"\bRSSI:\s*(-?\d+)");
        string link = Match(@"\bLink speed:\s*(\d+Mbps)");
        return new(MonitoringService.ParseSsid(text), ip.Length > 0 ? ip : "IP unavailable", signal.Length > 0 ? signal + " dBm" : "Signal unavailable", link.Length > 0 ? link : "Link unavailable");
    }

    public static IReadOnlyList<WifiNetwork> ParseNetworks(string text)
    {
        var result = new List<WifiNetwork>();
        foreach (string line in text.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*[\da-fA-F:]{17}\s+(\d+)\s+(-?\d+)(?:\([^)]*\))?\s+([\d.]+)\s+(.*?)\s+(\[.*\])\s*$");
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[4].Value)
                || !int.TryParse(match.Groups[1].Value, out int frequency) || !int.TryParse(match.Groups[2].Value, out int signal)
                || !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double age)
                || !double.IsFinite(age) || age < 0 || frequency is < 2000 or > 8000 || signal is < -127 or > 0) continue;
            string flags = match.Groups[5].Value.ToUpperInvariant();
            string security = flags.Contains("EAP") ? "enterprise" : flags.Contains("PSK") ? "wpa2" : flags.Contains("SAE") ? "wpa3" : flags.Contains("OWE") ? "owe" : flags.Contains("WEP") ? "wep" : "open";
            result.Add(new(match.Groups[4].Value, security, signal, frequency, age));
        }
        return result.GroupBy(n => (n.Ssid, n.Security)).Select(g => g.OrderByDescending(n => n.Signal).First()).OrderByDescending(n => n.Signal).ThenBy(n => n.Ssid).ToArray();
    }
}
