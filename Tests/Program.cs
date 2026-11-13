using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using QuestTool.Core;

if (args.SequenceEqual(new[] { "--mirror-process-test" }))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

if (args.SequenceEqual(new[] { "--apps-check" }))
{
    int count = 0;
    await AppChecks.RunAsync((condition, label) =>
    {
        if (!condition) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS " + label); count++;
    });
    Console.WriteLine($"{count} app checks passed.");
    return;
}

if (args.Length == 3 && args[0] == "--apps-device-check")
{
    var service = new AppService(new AdbClient(args[1]));
    var target = new DeploymentTarget(args[2], args[2], "");
    var apps = await service.ListAsync(target, false, default);
    if (apps.Count == 0) throw new Exception("Expected installed user apps.");
    string details = await service.ReadDetailsAsync(target, apps[0].Package, default);
    if (details.Contains("Version: Unavailable") || details.Contains("Version code: Unavailable")) throw new Exception("Version details missing.");
    Console.WriteLine($"PASS Read {apps.Count} installed user apps and selected app version/details from connected headset.");
    return;
}

if (args.SequenceEqual(new[] { "--mirror-stop-check" }))
{
    int count = 0;
    await MediaChecks.MirrorStopAsync((condition, label) =>
    {
        if (!condition) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS " + label); count++;
    });
    Console.WriteLine($"{count} OculusMirror process-stop checks passed.");
    return;
}

if (args.Length == 3 && args[0] == "--device-details-check")
{
    var adb = new AdbClient(args[1]);
    var target = new DeploymentTarget(args[2], args[2], "");
    foreach (var metric in await new MonitoringService(adb).ReadDetailsAsync(target, default)) Console.WriteLine(metric.Name + ": " + metric.Value);
    var scan = await new WifiService(adb).ScanAsync(target, default);
    Console.WriteLine("Current Wi-Fi: " + scan.Current.Summary);
    Console.WriteLine("Available networks: " + scan.Networks.Count);
    foreach (var network in scan.Networks) Console.WriteLine(network.Description);
    if (scan.Networks.Count == 0 || scan.Current.Ssid is null) throw new Exception("Expected live Wi-Fi status and scan results from this headset.");
    return;
}

if (args.Length == 4 && args[0] == "--casting-check")
{
    var service = new MediaService(new AdbClient(args[1]), new AdbClient(args[2], "Meta VR CLI"));
    try
    {
        await service.StartStreamingAsync(new(args[3], args[3], ""), default);
        await Task.Delay(3000);
        if (!await service.RefreshStreamingAsync(default)) throw new Exception("Casting exited after startup.");
        Console.WriteLine("PASS Production casting launch and resource directory; client remains running.");
    }
    finally { await service.StopStreamingAsync(default); }
    return;
}

if (args.SequenceEqual(new[] { "--monitoring-check" }))
{
    int count = 0;
    await MonitoringChecks.RunAsync((condition, label) =>
    {
        if (!condition) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS " + label); count++;
    });
    Console.WriteLine($"{count} monitoring, log, and Wi-Fi checks passed.");
    return;
}

if (args.Length == 4 && args[0] == "--files-list-check")
{
    var target = new DeploymentTarget(args[2], args[3], DeviceService.IsEndpoint(args[3]) ? args[3] : "");
    var files = new FileService(new AdbClient(args[1]));
    for (int attempt = 0; attempt < 3; attempt++)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var entries = await files.ListAsync(target, FileService.Root, default);
        timer.Stop();
        if (entries.Any(f => f.Modified is null)) throw new Exception("Missing modification date in headset listing.");
        Console.WriteLine($"PASS Wi-Fi: {entries.Count} shared-storage items with modification dates loaded in {timer.ElapsedMilliseconds} ms (request {attempt + 1}).");
    }
    return;
}

if (args.Length == 4 && args[0] == "--files-check")
{
    await FileChecks.HardwareAsync(args[1], args[2], args[3]);
    return;
}

// Optional hardware check uses the same production Wi-Fi setup path as the window.
if (args.Length == 3 && args[0] == "--wifi-check")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try
    {
        string endpoint = await new DeviceService(new AdbClient(args[1])).EnableWifiAsync(args[2], args[2], timeout.Token, Console.WriteLine);
        Console.WriteLine("VERIFIED Wi-Fi: " + endpoint);
    }
    catch (WifiConnectionException ex) { Console.WriteLine("NETWORK BLOCKED (address retained: " + ex.Endpoint + "): " + ex.Message); Environment.ExitCode = 2; }
    return;
}

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); passed++; Console.WriteLine("PASS " + label); }
void Throws(Action action, string label) { try { action(); } catch (Exception) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
string temp = Path.Combine(Path.GetTempPath(), "QuestTool-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    foreach (bool utf8 in new[] { true, false })
    {
        string file = Path.Combine(temp, utf8 ? "utf8.apk" : "utf16.apk");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var manifest = zip.CreateEntry("AndroidManifest.xml").Open()) manifest.Write(Manifest(utf8));
        var info = ApkInfo.Read(file);
        Check(info.Package == "com.example.demo" && info.VersionCode == 42 && info.VersionName == "1.2.3", "Read binary APK manifest " + (utf8 ? "UTF8" : "UTF16"));
    }
    Throws(() => ApkInfo.Parse("bad.apk", [3, 0, 8, 0, 8, 0, 0, 0]), "Reject manifest without root");
    Throws(() => ApkInfo.Parse("split.apk", Manifest(true, true)), "Reject split APK");
    Check(ApkInfo.Parse("major.apk", Manifest(true, false, 2)).VersionCode == (2L << 32) + 42, "Read 64-bit version code");
    var apk = ApkInfo.Read(Path.Combine(temp, "utf8.apk"));
    string main = Path.Combine(temp, "main.40.com.example.demo.obb"); File.WriteAllBytes(main, [1, 2, 3]);
    string patch = Path.Combine(temp, "patch.42.com.example.demo.obb"); File.WriteAllBytes(patch, [4, 5]);
    apk.ValidateObbs([main, patch]); Check(true, "Allow main/patch and retained OBB version");
    string wrong = Path.Combine(temp, "main.42.com.other.app.obb"); File.WriteAllText(wrong, "bad");
    Throws(() => apk.ValidateObbs([wrong]), "Reject wrong-package OBB");
    Throws(() => apk.ValidateObbs([main, main]), "Reject duplicate OBB");
    string another = Path.Combine(temp, "main.41.com.example.demo.obb"); File.WriteAllText(another, "bad");
    Throws(() => apk.ValidateObbs([main, another]), "Reject two main OBBs");
    string empty = Path.Combine(temp, "main.42.com.example.demo.obb"); File.WriteAllBytes(empty, []);
    Throws(() => apk.ValidateObbs([empty]), "Reject empty OBB");
    string future = Path.Combine(temp, "main.43.com.example.demo.obb"); File.WriteAllText(future, "future");
    Throws(() => apk.ValidateObbs([future]), "Reject OBB newer than APK");
    string invalidCode = Path.Combine(temp, "main.999999999999999999999.com.example.demo.obb"); File.WriteAllText(invalidCode, "bad");
    Throws(() => apk.ValidateObbs([invalidCode]), "Reject invalid OBB version number");
    Check(apk.ObbDirectory == "/sdcard/Android/obb/com.example.demo", "Derive OBB folder from APK package");
    Check(DeviceService.NetworkHosts("198.51.100.37/24").Count() == 254, "Bound subnet discovery");
    Check(DeviceService.NetworkHosts("198.51.100.37/32").Single() == "198.51.100.37", "Single-host discovery");
    Throws(() => DeviceService.NetworkHosts("10.0.0.0/8").ToArray(), "Reject oversized scan");
    Check(!DeviceService.IsEndpoint("198.51.100.1:5555;whoami") && DeviceService.IsEndpoint("198.51.100.1:5555"), "Validate network endpoints");
    var target = new DeploymentTarget("SERIAL", "USB", "");
    async Task<(FakeAdb Adb, List<DeploymentUpdate> Events)> Deploy(Action<FakeAdb>? configure = null, CancellationToken token = default, string[]? expansions = null)
    {
        var fake = new FakeAdb(); configure?.Invoke(fake);
        var events = new List<DeploymentUpdate>();
        await new DeploymentService(fake).DeployAsync(target, apk, expansions ?? [], false, new InlineProgress<DeploymentUpdate>(events.Add), token);
        return (fake, events);
    }
    var success = await Deploy();
    Check(success.Events.Last().Message == "Updated · verified", "Install and verify APK");
    Check(success.Adb.Commands.Where(c => c.Contains("install") || c.Contains("shell")).All(c => c[0] == "-s" && c[1] == "USB"), "Explicit device on every mutating command");
    Check(success.Adb.Commands.Any(c => c.Contains("install") && c.Contains("-r")) && !success.Adb.Commands.Any(c => c.Contains("uninstall")), "Preserve app data without uninstall");
    var downgrade = await Deploy(f => f.InstalledVersion = 99);
    Check(!downgrade.Adb.Commands.Any(c => c.Contains("install")) && downgrade.Events.Last().Message.Contains("Downgrade blocked"), "Block downgrade before install");
    var identity = await Deploy(f => f.Serial = "DIFFERENT");
    Check(!identity.Adb.Commands.Any(c => c.Contains("install")), "Block installation to wrong serial");
    var foreign = await Deploy(f => f.Manufacturer = "Other");
    Check(!foreign.Adb.Commands.Any(c => c.Contains("install")), "Block non-Quest device");
    var storage = await Deploy(f => f.AvailableKb = 1);
    Check(!storage.Adb.Commands.Any(c => c.Contains("install")), "Block insufficient storage");
    var signature = await Deploy(f => f.InstallFailure = true);
    Check(signature.Events.Last().Message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") && !signature.Adb.Commands.Any(c => c.Contains("push")), "Surface signing failure before OBB transfer");
    var expansionSuccess = await Deploy(expansions: [main, patch]);
    Check(expansionSuccess.Events.Last().Message == "Updated · verified" && expansionSuccess.Adb.Commands.Count(c => c.Contains("mv")) == 2, "Stage, size-check, and replace both OBBs");
    Check(expansionSuccess.Adb.Commands.Where(c => c.Contains("mv")).Select(c => c.Last()).Order().SequenceEqual(new[] { apk.ObbDirectory + "/" + Path.GetFileName(main), apk.ObbDirectory + "/" + Path.GetFileName(patch) }.Order()), "Keep original OBB filenames in exact APK package folder");
    var partial = await Deploy(f => f.PushFailure = true, expansions: [main]);
    Check(partial.Events.Last().Message.StartsWith("Partial update") && !partial.Adb.Commands.Any(c => c.Contains("mv")), "Report partial update and retain old OBB after failed transfer");
    Check(partial.Adb.Commands.Any(c => c.Contains("rm") && c.Last().Contains(".questtool-") && c.Last().EndsWith(".part")), "Clean only our staged OBB");
    var mismatch = await Deploy(f => f.WrongObbSize = true, expansions: [main]);
    Check(!mismatch.Adb.Commands.Any(c => c.Contains("mv")), "Reject incomplete OBB before replacement");
    var corrupt = await Deploy(f => f.WrongObbHash = true, expansions: [main]);
    Check(!corrupt.Adb.Commands.Any(c => c.Contains("mv")) && corrupt.Events.Last().Message.Contains("checksum"), "Reject same-size corrupted OBB before replacement");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var cancelled = await Deploy(token: cancel.Token);
    Check(cancelled.Events.Last().Message.StartsWith("Cancelled") && !cancelled.Adb.Commands.Any(c => c.Contains("install")), "Cancel before installation");
    var verification = await Deploy(f => f.VerifyWrongVersion = true);
    Check(verification.Events.Last().Message.StartsWith("Partial update"), "Reject unverified installed version");
    var shared = new FakeAdb { InstallDelay = 40 };
    var service = new DeploymentService(shared);
    await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.DeployAsync(target, apk, [], false, new InlineProgress<DeploymentUpdate>(_ => { }), default)));
    Check(shared.PeakInstalls == 2, "Limit concurrent installations to two");
    var queueAdb = new FakeAdb();
    var queueService = new DeploymentService(queueAdb);
    using var offlineCancel = new CancellationTokenSource();
    var offlineEvents = new List<DeploymentUpdate>();
    var offline = queueService.DeployAsync(new("SERIAL", "OFFLINE", ""), apk, [], true, new InlineProgress<DeploymentUpdate>(offlineEvents.Add), offlineCancel.Token);
    var readyEvents = new List<DeploymentUpdate>();
    await queueService.DeployAsync(target, apk, [], false, new InlineProgress<DeploymentUpdate>(readyEvents.Add), default);
    offlineCancel.Cancel(); await offline;
    Check(readyEvents.Last().Message == "Updated · verified" && offlineEvents.Any(e => e.Message.StartsWith("Waiting for headset")), "Offline queue does not occupy transfer slots");
    Check(offlineEvents.Last().Message.StartsWith("Cancelled"), "Cancel an offline wait promptly");
    var wifi = new WifiAdb();
    string wifiEndpoint = await new DeviceService(wifi, (_, _, _) => Task.FromResult(true)).EnableWifiAsync("USB", "SERIAL", default);
    Check(wifiEndpoint == "192.0.2.3:5555" && wifi.Commands.Any(c => c.Contains("tcpip")), "Enable wireless ADB and verify headset serial");
    var alreadyEnabled = new WifiAdb { Enabled = true };
    await new DeviceService(alreadyEnabled, (_, _, _) => Task.FromResult(true)).EnableWifiAsync("USB", "SERIAL", default);
    Check(!alreadyEnabled.Commands.Any(c => c.Contains("tcpip")), "Do not restart ADB when port 5555 is already enabled");
    var blockedWifi = new WifiAdb { Enabled = true, Blocked = true };
    try { await new DeviceService(blockedWifi, (_, _, _) => Task.FromResult(true)).EnableWifiAsync("USB", "SERIAL", default); throw new Exception("Expected Wi-Fi connection failure"); }
    catch (WifiConnectionException ex)
    {
        Check(ex.Endpoint == "192.0.2.3:5555" && ex.Message.Contains("enabled") && ex.Message.Contains("PC could not connect"), "Retain enabled endpoint and distinguish network failure");
        Check(blockedWifi.Commands.Count(c => c[0] == "connect") == 1, "Do not repeat network timeouts as ADB restart retries");
    }
    var wrongWifi = new WifiAdb { Enabled = true, WifiSerial = "OTHER" };
    try { await new DeviceService(wrongWifi, (_, _, _) => Task.FromResult(true)).EnableWifiAsync("USB", "SERIAL", default); throw new Exception("Expected identity rejection"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("different headset"), "Reject wrong Wi-Fi identity without misreporting a network timeout"); }
    var noWifi = new WifiAdb { Routes = "" };
    try { await new DeviceService(noWifi).EnableWifiAsync("USB", "SERIAL", default); throw new Exception("Expected missing Wi-Fi rejection"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("No headset Wi-Fi IP") && !noWifi.Commands.Any(c => c.Contains("tcpip")), "Require headset network address before enabling ADB"); }
    var unreachable = new WifiAdb { Enabled = true };
    try { await new DeviceService(unreachable, (_, _, _) => Task.FromResult(false)).EnableWifiAsync("USB", "SERIAL", default); throw new Exception("Expected unreachable Wi-Fi rejection"); }
    catch (WifiConnectionException ex)
    {
        Check(ex.Endpoint == "192.0.2.3:5555" && ex.Message.Contains("TCP port"), "Retain the current USB-discovered address when guest Wi-Fi is unreachable");
        Check(!unreachable.Commands.Any(c => c[0] == "connect"), "Skip slow ADB connect attempts when TCP reachability fails");
    }
    await FileChecks.RunAsync(Check, temp);
    await MediaChecks.RunAsync(Check, temp);
    await MonitoringChecks.RunAsync(Check);
    await NetworkDetailsChecks.RunAsync(Check);
    Console.WriteLine($"\n{passed} checks passed.");
}
finally { Directory.Delete(temp, true); }

static byte[] Manifest(bool utf8, bool split = false, uint major = 0)
{
    string[] strings = ["manifest", "package", "com.example.demo", "versionCode", "versionName", "1.2.3", "http://schemas.android.com/apk/res/android", "split", "config.en", "versionCodeMajor"];
    using var poolData = new MemoryStream(); using var stringsWriter = new BinaryWriter(poolData, Encoding.UTF8, true);
    var offsets = new List<uint>();
    foreach (var s in strings)
    {
        offsets.Add((uint)poolData.Position);
        if (utf8) { var b = Encoding.UTF8.GetBytes(s); stringsWriter.Write((byte)s.Length); stringsWriter.Write((byte)b.Length); stringsWriter.Write(b); stringsWriter.Write((byte)0); }
        else { stringsWriter.Write((ushort)s.Length); stringsWriter.Write(Encoding.Unicode.GetBytes(s)); stringsWriter.Write((ushort)0); }
    }
    while (poolData.Length % 4 != 0) stringsWriter.Write((byte)0);
    using var body = new MemoryStream(); using var writer = new BinaryWriter(body, Encoding.UTF8, true);
    int poolSize = 28 + strings.Length * 4 + (int)poolData.Length;
    writer.Write((ushort)1); writer.Write((ushort)28); writer.Write(poolSize); writer.Write(strings.Length); writer.Write(0); writer.Write(utf8 ? 0x100 : 0); writer.Write(28 + strings.Length * 4); writer.Write(0);
    foreach (uint offset in offsets) writer.Write(offset);
    writer.Write(poolData.ToArray());
    int count = 3 + (split ? 1 : 0) + (major > 0 ? 1 : 0);
    writer.Write((ushort)0x102); writer.Write((ushort)16); writer.Write(36 + count * 20); writer.Write(1); writer.Write(uint.MaxValue);
    writer.Write(uint.MaxValue); writer.Write(0u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)count); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
    void Attribute(uint ns, uint name, byte type, uint value) { writer.Write(ns); writer.Write(name); writer.Write(type == 3 ? value : uint.MaxValue); writer.Write((ushort)8); writer.Write((byte)0); writer.Write(type); writer.Write(value); }
    Attribute(uint.MaxValue, 1, 3, 2); Attribute(6, 3, 0x10, 42); Attribute(6, 4, 3, 5);
    if (split) Attribute(uint.MaxValue, 7, 3, 8);
    if (major > 0) Attribute(6, 9, 0x10, major);
    using var result = new MemoryStream(); using var output = new BinaryWriter(result);
    output.Write((ushort)3); output.Write((ushort)8); output.Write(8 + (int)body.Length); output.Write(body.ToArray());
    return result.ToArray();
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
sealed class WifiAdb : IAdb
{
    public bool Enabled, Blocked;
    public string WifiSerial = "SERIAL", Routes = "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.3";
    public List<string[]> Commands = [];
    public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
    {
        cancellation.ThrowIfCancellationRequested();
        var a = arguments.ToArray(); Commands.Add(a);
        if (a.Last() == DeviceService.IdentityCommand) return Task.FromResult(new CommandResult(0, "Oculus\nQuest 3\n" + (a[1] == "USB" ? "SERIAL" : WifiSerial)));
        if (a[0] == "connect")
        {
            if (Blocked) throw new TimeoutException("ADB connection timed out.");
            return Task.FromResult(new CommandResult(0, "connected to " + a[1]));
        }
        string value = a.Contains("getprop") ? a.Last() switch
        {
            "ro.product.manufacturer" => "Oculus",
            "ro.product.model" => "Quest 3",
            "ro.serialno" => a[1] == "USB" ? "SERIAL" : WifiSerial,
            "service.adb.tcp.port" => Enabled ? "5555" : "",
            _ => ""
        } : a.Last() == "route" ? Routes : "restarting in TCP mode port: 5555";
        return Task.FromResult(new CommandResult(0, value));
    }
}
sealed class FakeAdb : IAdb
{
    public string Serial = "SERIAL", Manufacturer = "Meta";
    public long InstalledVersion = 41, AvailableKb = 10000000;
    public bool InstallFailure, PushFailure, WrongObbSize, WrongObbHash, VerifyWrongVersion;
    public int InstallDelay, PeakInstalls;
    int installs;
    readonly Dictionary<string, long> pushed = [];
    readonly Dictionary<string, string> hashes = [];
    public List<string[]> Commands { get; } = [];
    public async Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
    {
        cancellation.ThrowIfCancellationRequested();
        var a = arguments.ToArray(); lock (Commands) Commands.Add(a);
        if (a.Contains("OFFLINE")) return new(1, "device offline");
        if (a.Last() == DeviceService.IdentityCommand) return new(0, Manufacturer + "\nQuest 3\n" + Serial);
        if (a.Contains("getprop")) return new(0, a.Last() switch { "ro.product.manufacturer" => Manufacturer, "ro.product.model" => "Quest 3", _ => Serial });
        if (a.Contains("dumpsys")) return new(0, "Packages:\n versionCode=" + InstalledVersion + " minSdk=29");
        if (a.Contains("df")) return new(0, $"Filesystem 1K-blocks Used Available Use% Mounted on\n/data 20000000 1 {AvailableKb} 1% /data");
        if (a.Contains("install"))
        {
            if (InstallFailure) return new(1, "Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]");
            int running = Interlocked.Increment(ref installs); lock (Commands) PeakInstalls = Math.Max(PeakInstalls, running);
            try { if (InstallDelay > 0) await Task.Delay(InstallDelay, cancellation); InstalledVersion = VerifyWrongVersion ? 40 : 42; return new(0, "Performing Streamed Install\nSuccess"); }
            finally { Interlocked.Decrement(ref installs); }
        }
        if (a.Contains("push")) { if (PushFailure) return new(1, "adb: error: connection lost"); pushed[a.Last()] = new FileInfo(a[^2]).Length; hashes[a.Last()] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a[^2]))); return new(0, "1 file pushed"); }
        if (a.Contains("stat")) return new(0, (WrongObbSize ? 0 : pushed[a.Last()]).ToString());
        if (a.Contains("sha256sum")) return new(0, (WrongObbHash ? new string('0', 64) : hashes[a.Last()]) + "  " + a.Last());
        if (a.Contains("mv")) { pushed[a.Last()] = pushed[a[^2]]; return new(0, ""); }
        return new(0, "");
    }
}
