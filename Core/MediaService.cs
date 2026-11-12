using System.Text.Json;

namespace QuestTool.Core;

public sealed record RecordingSession(DeploymentTarget Target, string Destination, string StagingPath);

// The CLI owns the headset-side capture and the native Meta casting window.
// Keep session targets fixed so changing a picker cannot stop another headset.
public sealed class MediaService(IAdb adb, IAdb metaVr)
{
    bool recordingDownloaded;
    public DeploymentTarget? StreamingTarget { get; private set; }
    public RecordingSession? Recording { get; private set; }
    public Dictionary<string, DeploymentTarget> DisabledProximity { get; } = [];

    async Task<CommandResult> RunAsync(string transport, string[] command, CancellationToken token, int timeout = 30) =>
        await metaVr.RunAsync(["--device", transport, "--json", .. command], token, timeout);

    static string Success(CommandResult result)
    {
        string output = result.RequireSuccess();
        // Some CLI releases report operation failures in a JSON envelope.
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                ((root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                || (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))))
                throw new IOException(output);
        }
        catch (JsonException) { }
        return output;
    }

    async Task<string> ResolveAsync(DeploymentTarget target, CancellationToken token) =>
        await new DeviceService(adb).ResolveAsync(target, token);

    public async Task<bool> RefreshStreamingAsync(CancellationToken token)
    {
        string output = Success(await metaVr.RunAsync(["--json", "casting", "status"], token));
        try
        {
            using var document = JsonDocument.Parse(output);
            bool running = document.RootElement.GetProperty("running").GetBoolean();
            if (!running) StreamingTarget = null;
            return running;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Cannot read casting status. Choose a Meta VR CLI version that supports 'casting status' in Setup. " + output, ex);
        }
    }

    public async Task StartStreamingAsync(DeploymentTarget target, CancellationToken token)
    {
        if (await RefreshStreamingAsync(token))
            throw new InvalidOperationException("A Meta casting window is already running. Close it before starting another stream.");
        string transport = await ResolveAsync(target, token);
        // Once a mutating command starts, let it finish even if Cancel is clicked,
        // so a successful start is always tracked for shutdown and recovery.
        token.ThrowIfCancellationRequested();
        try
        {
            IAdb launcher = metaVr;
            if (metaVr is AdbClient client)
            {
                string toolInfo = Success(await metaVr.RunAsync(["--json", "tools", "info", "casting"], token));
                string directory = CastingDirectory(toolInfo);
                if (!Directory.Exists(directory)) throw new IOException("The casting client directory is missing. Install / repair casting client in Setup.");
                // The native client loads resources relative to its working directory.
                // CLI launch otherwise succeeds while the window crashes at startup.
                launcher = client.InDirectory(directory);
            }
            token.ThrowIfCancellationRequested();
            Success(await launcher.RunAsync(["--device", transport, "--json", "casting", "start"], CancellationToken.None, 60));
        }
        catch (Exception ex) when (ex.Message.Contains("casting client is not installed", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Meta's standalone casting client is missing. In Setup, click Install / repair casting client, then retry. Developer Hub's bundled client is a separate installation.", ex);
        }
        StreamingTarget = target with { Transport = transport };
        await Task.Delay(1500, CancellationToken.None);
        if (!await RefreshStreamingAsync(CancellationToken.None))
            throw new IOException("The casting client exited during startup. Install / repair the client in Setup, or choose OculusMirror for an active Link session.");
    }

    public static string CastingDirectory(string toolInfo)
    {
        using var document = JsonDocument.Parse(toolInfo);
        if (!document.RootElement.TryGetProperty("install_path", out var path) || path.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(path.GetString()) || !Path.IsPathFullyQualified(path.GetString()!))
            throw new IOException("Meta's casting client is not installed. Click Install / repair casting client in Setup.");
        return path.GetString()!;
    }

    public async Task StopStreamingAsync(CancellationToken token)
    {
        var target = StreamingTarget;
        if (target is null || !await RefreshStreamingAsync(token)) return;
        Success(await RunAsync(target.Transport, ["casting", "stop"], CancellationToken.None));
        StreamingTarget = null;
    }

    public async Task StartRecordingAsync(DeploymentTarget target, string destination, int width, int height, CancellationToken token)
    {
        if (Recording is not null) throw new InvalidOperationException("Stop and save the current recording first.");
        if (width is < 256 or > 3840 || height is < 256 or > 2160 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Choose an even recording size between 256×256 and 3840×2160.");
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an MP4 filename.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose a new filename; that recording already exists.");
        string folder = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing recording folder.");
        string staging = Path.Combine(folder, ".questtool-recording-" + Guid.NewGuid().ToString("N") + ".mp4");
        // Check local write access before starting a headset-side recording.
        using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write)) { }
        File.Delete(staging);
        string transport = await ResolveAsync(target, token);
        token.ThrowIfCancellationRequested();
        Success(await RunAsync(transport, ["capture", "video", "start", "--width", width.ToString(), "--height", height.ToString()], CancellationToken.None));
        Recording = new(target with { Transport = transport }, destination, staging);
        recordingDownloaded = false;
    }

    public async Task<string?> StopRecordingAsync(CancellationToken token)
    {
        var recording = Recording;
        if (recording is null) return null;
        // If publication failed after a completed pull, retry publishing locally.
        if (!recordingDownloaded)
        {
            string transport = await ResolveAsync(recording.Target, token);
            token.ThrowIfCancellationRequested();
            Success(await RunAsync(transport, ["capture", "video", "stop", "--pull", recording.StagingPath], CancellationToken.None, 300));
            recordingDownloaded = File.Exists(recording.StagingPath) && new FileInfo(recording.StagingPath).Length > 0;
        }
        if (!File.Exists(recording.StagingPath) || new FileInfo(recording.StagingPath).Length == 0)
            throw new IOException("No video was downloaded. Check the headset connection and try Stop & save again. The recording session is kept for recovery.");
        File.Move(recording.StagingPath, recording.Destination, overwrite: false);
        Recording = null;
        recordingDownloaded = false;
        return recording.Destination;
    }

    public void SetRecordingDestination(string destination)
    {
        if (Recording is null) throw new InvalidOperationException("No recording to save.");
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("Choose an MP4 filename in an existing folder.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose a new recording filename.");
        Recording = Recording with { Destination = destination };
    }

    public async Task SetProximityDisabledAsync(DeploymentTarget target, bool disabled, CancellationToken token)
    {
        string transport = await ResolveAsync(target, token);
        token.ThrowIfCancellationRequested();
        Success(await RunAsync(transport, ["device", "proximity", disabled ? "disable" : "enable"], CancellationToken.None));
        if (disabled) DisabledProximity[target.Serial] = target with { Transport = transport };
        else DisabledProximity.Remove(target.Serial);
    }

    public static string FindTool()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "metavr.exe"), Path.Combine(profile, ".metavr", "bin", "metavr.exe") };
        string cache = Path.Combine(profile, ".metavr", "npm", "binaries", "@meta-quest__metavr-win32-x64");
        if (Directory.Exists(cache))
            candidates.AddRange(Directory.EnumerateDirectories(cache).OrderByDescending(p => Directory.GetLastWriteTimeUtc(p)).Select(p => Path.Combine(p, "metavr.exe")));
        foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(part)) candidates.Add(Path.Combine(part.Trim('"'), "metavr.exe"));
        foreach (string hub in new[] { "Meta VR Developer Hub", "Meta Quest Developer Hub" })
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), hub, "resources", "bin", "metavr.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
