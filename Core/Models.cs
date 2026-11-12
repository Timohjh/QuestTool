using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace QuestTool.Core;

public sealed class Headset : INotifyPropertyChanged
{
    string name = "", endpoint = "", model = "", connection = "Offline", result = "", version = "—", wifiStatus = "";
    bool selected, active;
    IReadOnlyList<DeviceMetric> details = [];
    public IReadOnlyList<DeviceMetric> Details { get => details; set => Set(ref details, value); }
    string battery = "—", storage = "—", network = "—", lastSeen = "Never", monitorStatus = "Not checked";
    public string Battery { get => battery; set => Set(ref battery, value); }
    public string Storage { get => storage; set => Set(ref storage, value); }
    public string Network { get => network; set => Set(ref network, value); }
    public string LastSeen { get => lastSeen; set => Set(ref lastSeen, value); }
    public string MonitorStatus { get => monitorStatus; set => Set(ref monitorStatus, value); }
    public string Serial { get; set; } = "";
    public string Name { get => name; set => Set(ref name, value); }
    public string Endpoint { get => endpoint; set => Set(ref endpoint, value); }
    public string Model { get => model; set => Set(ref model, value); }
    public string Connection { get => connection; set => Set(ref connection, value); }
    public string Result { get => result; set => Set(ref result, value); }
    public string Version { get => version; set => Set(ref version, value); }
    public string WifiStatus { get => wifiStatus; set => Set(ref wifiStatus, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public bool Active { get => active; set => Set(ref active, value); }
    public string Transport { get; set; } = "";
    public string UsbTransport { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? key = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(key));
    }
}

public sealed class Settings
{
    public string AdbPath { get; set; } = "";
    public string MetaVrPath { get; set; } = "";
    public string RecordingFolder { get; set; } = "";
    public bool AutoWifi { get; set; } = true;
    public string MirrorPath { get; set; } = "";
    public MirrorOptions Mirror { get; set; } = new();
    public int StreamingBackend { get; set; }
    public List<RememberedHeadset> Headsets { get; set; } = [];
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestTool");
    public static Settings Load()
    {
        var file = Path.Combine(Folder, "settings.json");
        if (!File.Exists(file)) return new();
        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}
public sealed record RememberedHeadset(string Serial, string Name, string Endpoint, string Model);
public sealed record CommandResult(int ExitCode, string Output)
{
    public string RequireSuccess()
    {
        if (ExitCode != 0 || Output.Contains("Failure [", StringComparison.OrdinalIgnoreCase) || Output.StartsWith("adb: error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Output.Trim());
        return Output.Trim();
    }
}

public interface IAdb
{
    Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30);
}
