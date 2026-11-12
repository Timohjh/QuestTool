using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed record DeviceIdentity(string Serial, string Model);
public sealed record ConnectedHeadset(DeviceIdentity Identity, string Transport, bool Wireless);
public sealed class WifiConnectionException(string endpoint, string message) : IOException(message)
{
    public string Endpoint { get; } = endpoint;
}

public sealed class DeviceService(IAdb adb, Func<string, int, CancellationToken, Task<bool>>? portProbe = null)
{
    internal const string IdentityCommand = "getprop ro.product.manufacturer; getprop ro.product.model; getprop ro.serialno";

    public async Task<DeviceIdentity> IdentifyAsync(string transport, CancellationToken token)
    {
        string output = (await adb.RunAsync(["-s", transport, "shell", IdentityCommand], token, 8)).RequireSuccess();
        string[] properties = output.Split('\n').Select(p => p.Trim()).ToArray();
        if (properties.Length != 3) throw new InvalidOperationException("Cannot verify headset identity. Refresh the device list.");
        string manufacturer = properties[0], model = properties[1], serial = properties[2];
        if (!(manufacturer.Equals("Oculus", StringComparison.OrdinalIgnoreCase) || manufacturer.Equals("Meta", StringComparison.OrdinalIgnoreCase)) || !model.Contains("Quest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{transport} is not a recognized Meta Quest ({manufacturer} {model}).");
        if (string.IsNullOrWhiteSpace(serial) || serial.Contains('\n')) throw new InvalidOperationException("Cannot verify the headset serial number.");
        return new(serial, model);
    }

    public async Task<List<ConnectedHeadset>> ListAsync(Action<string> notice, CancellationToken token)
    {
        var output = (await adb.RunAsync(["devices", "-l"], token)).RequireSuccess();
        var result = new List<ConnectedHeadset>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || line.StartsWith("List of") || line.StartsWith('*')) continue;
            if (parts[1] is "unauthorized" or "offline") { notice($"{parts[0]}: {parts[1]}. For authorization, accept USB debugging inside the headset."); continue; }
            if (parts[1] != "device") continue;
            try { result.Add(new(await IdentifyAsync(parts[0], token), parts[0], IsEndpoint(parts[0]))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notice(ex.Message); }
        }
        return result;
    }

    public static bool IsEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && int.TryParse(parts[1], out int port) && port is > 0 and <= 65535;
    }

    public async Task ConnectAsync(string endpoint, CancellationToken token)
    {
        if (!IsEndpoint(endpoint)) throw new ArgumentException("Enter an IPv4 address and port, for example 192.168.1.42:5555.");
        var output = (await adb.RunAsync(["connect", endpoint], token, 8)).RequireSuccess();
        if (!output.Contains("connected to", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(output);
    }

    public async Task<string> EnableWifiAsync(string usbTransport, string expectedSerial, CancellationToken token, Action<string>? report = null)
    {
        if (IsEndpoint(usbTransport) || string.IsNullOrWhiteSpace(usbTransport)) throw new InvalidOperationException("Connect this headset by USB first, then Refresh.");
        if ((await IdentifyAsync(usbTransport, token)).Serial != expectedSerial) throw new InvalidOperationException("USB headset identity changed. Refresh and select it again.");
        string routes = (await adb.RunAsync(["-s", usbTransport, "shell", "ip", "route"], token)).RequireSuccess();
        var address = routes.Split('\n').Where(l => l.Contains("wlan")).Select(l => Regex.Match(l, @"\bsrc\s+(\d+\.\d+\.\d+\.\d+)\b")).FirstOrDefault(m => m.Success);
        if (address is null) throw new InvalidOperationException("No headset Wi-Fi IP found. Connect the headset to Wi-Fi first.");
        string endpoint = address.Groups[1].Value + ":5555";
        string port = (await adb.RunAsync(["-s", usbTransport, "shell", "getprop", "service.adb.tcp.port"], token)).RequireSuccess();
        if (port != "5555")
        {
            report?.Invoke("Enabling wireless ADB on the headset…");
            (await adb.RunAsync(["-s", usbTransport, "tcpip", "5555"], token)).RequireSuccess();
            await Task.Delay(1000, token);
        }
        string failure = "Connection was not verified.";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(1000, token);
            report?.Invoke($"ADB enabled · connecting to {endpoint} ({attempt + 1}/3)…");
            bool reachable = portProbe is not null ? await portProbe(address.Groups[1].Value, 5555, token) : await PortOpenAsync(address.Groups[1].Value, token);
            if (!reachable) { failure = "The PC cannot reach TCP port 5555 at the headset's Wi-Fi address. Check guest-network isolation and routing."; continue; }
            try
            {
                await ConnectAsync(endpoint, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
                // A connection timeout usually indicates a network path problem, not an ADB restart delay.
                if (ex is TimeoutException) break;
                continue;
            }
            // Identity errors must not be reported as network failures or retried against a different device.
            if ((await IdentifyAsync(endpoint, token)).Serial != expectedSerial) throw new InvalidOperationException("Wi-Fi endpoint belongs to a different headset. Connection not accepted.");
            return endpoint;
        }
        string localAddresses = string.Join(", ", NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString()).Distinct());
        throw new WifiConnectionException(endpoint, $"Wireless ADB is enabled at {endpoint}, but the PC could not connect. PC LAN address(es): {localAddresses}. Connect the PC's Wi-Fi to the headset's network, or put the headset on the PC's main LAN. Guest/client isolation or routing/firewall rules may block access. Keep USB connected until Wi-Fi is confirmed. Details: {failure}");
    }

    public async Task<string> ResolveAsync(DeploymentTarget target, CancellationToken token)
    {
        var transports = new[] { target.Transport, target.Endpoint }.Where(t => !string.IsNullOrEmpty(t)).Distinct();
        var errors = new List<string>();
        foreach (string transport in transports)
        {
            try
            {
                if (IsEndpoint(transport)) await ConnectAsync(transport, token);
                if ((await IdentifyAsync(transport, token)).Serial != target.Serial) throw new InvalidOperationException("Headset identity does not match. Refresh the device list.");
                return transport;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); }
        }
        throw new IOException(errors.Count > 0 ? string.Join("; ", errors) : "Headset is offline. Reconnect and Refresh first.");
    }

    public static string DefaultNetwork()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var properties = adapter.GetIPProperties();
            if (!properties.GatewayAddresses.Any()) continue;
            var address = properties.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (address is null) continue;
            byte[] b = address.Address.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{b[2]}.0/24";
        }
        return "192.168.1.0/24";
    }

    public static IEnumerable<string> NetworkHosts(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1], out int bits) || bits is < 22 or > 32)
            throw new ArgumentException("Use an IPv4 subnet between /22 and /32, for example 192.168.1.0/24 (at most 1024 addresses).");
        var b = ip.GetAddressBytes();
        uint address = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint count = 1u << (32 - bits), start = address & (uint.MaxValue << (32 - bits));
        uint first = bits < 31 ? 1u : 0u, end = bits < 31 ? count - 1 : count;
        return Enumerable.Range((int)first, (int)(end - first)).Select(i => { uint a = start + (uint)i; return $"{a >> 24}.{(a >> 16) & 255}.{(a >> 8) & 255}.{a & 255}"; });
    }

    public static async Task<bool> PortOpenAsync(string host, CancellationToken token, int port = 5555)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(400);
        try { await client.ConnectAsync(host, port, timeout.Token); return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { token.ThrowIfCancellationRequested(); return false; }
    }
}
