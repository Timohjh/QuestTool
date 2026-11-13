using System.ComponentModel;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed class InstalledApp(string package) : INotifyPropertyChanged
{
    public string Package { get; } = package;
    public string Name => Package[(Package.LastIndexOf('.') + 1)..];
    string details = "Select this app to load its details.";
    public string Details
    {
        get => details;
        set { details = value; PropertyChanged?.Invoke(this, new(nameof(Details))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AppService(IAdb adb)
{
    static void ValidatePackage(string package)
    {
        if (!Regex.IsMatch(package, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+\z"))
            throw new ArgumentException("Invalid Android package name.");
    }

    public static IReadOnlyList<InstalledApp> ParseList(string output) => output.Split('\n')
        .Select(line => line.Trim()).Where(line => line.StartsWith("package:", StringComparison.Ordinal))
        .Select(line => line[8..]).Where(package => Regex.IsMatch(package, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+\z"))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Select(package => new InstalledApp(package)).ToArray();

    public static string ParseDetails(string package, string dump)
    {
        ValidatePackage(package);
        if (!dump.Contains("Package [" + package + "]", StringComparison.Ordinal))
            throw new IOException("App is no longer installed or its details are unavailable. Refresh the app list.");
        string Field(string name) => Regex.Match(dump, @"(?m)^\s*" + name + @"=([^\r\n]+)") is { Success: true } match ? match.Groups[1].Value.Trim() : "Unavailable";
        return $"Package: {package}\nVersion: {Field("versionName")}\nVersion code: {DeploymentService.ParseVersion(dump)?.ToString() ?? "Unavailable"}\nFirst installed: {Field("firstInstallTime")}\nLast updated: {Field("lastUpdateTime")}\nAPK path: {Field("codePath")}\nData folder: {Field("dataDir")}";
    }

    public async Task<IReadOnlyList<InstalledApp>> ListAsync(DeploymentTarget target, bool includeSystem, CancellationToken token)
    {
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        var args = new List<string> { "-s", transport, "shell", "pm", "list", "packages", "--user", "current" };
        if (!includeSystem) args.Add("-3");
        return ParseList((await adb.RunAsync(args, token)).RequireSuccess());
    }

    public async Task<string> ReadDetailsAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        return ParseDetails(package, (await adb.RunAsync(["-s", transport, "shell", "dumpsys", "package", package], token)).RequireSuccess());
    }

    public async Task LaunchAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        string? component = null;
        foreach (string category in new[] { "android.intent.category.LAUNCHER", "android.intent.category.INFO" })
        {
            string resolved = (await adb.RunAsync(["-s", transport, "shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", category, package], token)).RequireSuccess();
            component = resolved.Split('\n').Select(line => line.Trim()).FirstOrDefault(line =>
                line.StartsWith(package + "/", StringComparison.Ordinal) && Regex.IsMatch(line, @"\A[A-Za-z0-9_.]+/[A-Za-z0-9_.$]+\z"));
            if (component is not null) break;
        }
        if (component is null) throw new InvalidOperationException("This package has no launchable activity.");
        string output = (await adb.RunAsync(["-s", transport, "shell", "am", "start", "-W", "-n", "'" + component + "'"], token)).RequireSuccess();
        if (Regex.IsMatch(output, @"(?im)^\s*(Error|Exception|SecurityException)\b") || !Regex.IsMatch(output, @"(?im)^Status:\s*ok\s*$"))
            throw new IOException("App launch was not confirmed: " + output);
    }

    public async Task CloseAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        string output = (await adb.RunAsync(["-s", transport, "shell", "am", "force-stop", package], token)).RequireSuccess();
        if (!string.IsNullOrWhiteSpace(output)) throw new IOException("App close was not confirmed: " + output);
    }

    public async Task RestartAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        await CloseAsync(target, package, token);
        await LaunchAsync(target, package, token);
    }

    static void RequireConfirmation(string output, string action)
    {
        if (!Regex.IsMatch(output, @"\ASuccess\s*\z")) throw new IOException(action + " was not confirmed: " + output);
    }

    public async Task UninstallAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        RequireConfirmation((await adb.RunAsync(["-s", transport, "shell", "pm", "uninstall", "--user", "current", package], token, 120)).RequireSuccess(), "Uninstall");
    }

    public async Task ClearAsync(DeploymentTarget target, string package, bool cacheOnly, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        if (cacheOnly)
        {
            // Some Android versions print valid help with exit code 1.
            string help = (await adb.RunAsync(["-s", transport, "shell", "pm", "help"], token)).Output;
            if (!help.Contains("--cache-only", StringComparison.Ordinal))
                throw new NotSupportedException("This headset does not support cache-only clearing over ADB. Use App settings instead.");
        }
        var args = new List<string> { "-s", transport, "shell", "pm", "clear", "--user", "current" };
        if (cacheOnly) args.Add("--cache-only");
        args.Add(package);
        RequireConfirmation((await adb.RunAsync(args, token, 120)).RequireSuccess(), cacheOnly ? "Clear cache" : "Clear app data");
    }

    public async Task OpenSettingsAsync(DeploymentTarget target, string package, CancellationToken token)
    {
        ValidatePackage(package);
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        string output = (await adb.RunAsync(["-s", transport, "shell", "am", "start", "-W", "-a", "android.settings.APPLICATION_DETAILS_SETTINGS", "-d", "package:" + package], token)).RequireSuccess();
        if (!Regex.IsMatch(output, @"(?im)^Status:\s*ok\s*$") || Regex.IsMatch(output, @"(?im)^\s*(Error|Exception|SecurityException)\b"))
            throw new IOException("App settings could not be opened on this headset: " + output);
    }

    public static IReadOnlyList<string> ParseApkPaths(string output)
    {
        var paths = output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("package:", StringComparison.Ordinal)).Select(line => line[8..]).ToArray();
        if (paths.Length == 0 || paths.Any(path => !path.StartsWith('/') || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or "..")))
            throw new IOException("Installed APK paths are unavailable or invalid. Refresh the app list.");
        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<string> ExportAsync(DeploymentTarget target, string package, string parentFolder, IProgress<string>? progress, CancellationToken token)
    {
        ValidatePackage(package);
        if (!Directory.Exists(parentFolder)) throw new DirectoryNotFoundException("Choose an existing export folder.");
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        var paths = ParseApkPaths((await adb.RunAsync(["-s", transport, "shell", "pm", "path", "--user", "current", package], token)).RequireSuccess());
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(parentFolder, package + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id[..8]);
        string staging = Path.Combine(parentFolder, ".questtool-export-" + id);
        var temporaryFiles = new List<string>();
        if (Directory.Exists(staging) || Directory.Exists(destination)) throw new IOException("Export folder already exists. Choose another location.");
        Directory.CreateDirectory(staging);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string name = paths.Count == 1 ? package + ".apk" : $"{i:D2}-" + Path.GetFileName(paths[i]);
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("An APK filename is not supported on Windows.");
                string file = Path.Combine(staging, name);
                temporaryFiles.Add(file);
                progress?.Report($"Exporting APK {i + 1}/{paths.Count}…");
                (await adb.RunAsync(["-s", transport, "pull", paths[i], file], token, 3600)).RequireSuccess();
                if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IOException("APK export produced an empty or missing file.");
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            // Clean up only the exact temporary files created by this export.
            if (Directory.Exists(staging))
            {
                foreach (string file in temporaryFiles) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    public static string FilterLogs(string package, string processes, string logs)
    {
        ValidatePackage(package);
        var ids = processes.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 2 && int.TryParse(fields[0], out int pid) && pid > 0 && (fields[1] == package || fields[1].StartsWith(package + ":", StringComparison.Ordinal)))
            .Select(fields => fields[0]).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) throw new InvalidOperationException("This app is not running. Launch it and read logs again.");
        return string.Join('\n', logs.Split('\n').Where(line =>
        {
            var match = Regex.Match(line, @"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+(\d+)\s+");
            return match.Success && ids.Contains(match.Groups[1].Value);
        }));
    }

    public async Task<string> ReadLogsAsync(DeploymentTarget target, string package, char priority, CancellationToken token)
    {
        ValidatePackage(package);
        if (!"VDIWEF".Contains(priority)) throw new ArgumentException("Choose a valid log priority.");
        string transport = await new DeviceService(adb).ResolveAsync(target, token);
        string processes = (await adb.RunAsync(["-s", transport, "shell", "ps", "-A", "-o", "PID,NAME"], token)).RequireSuccess();
        string logs = (await adb.RunAsync(["-s", transport, "logcat", "-d", "-t", "1000", "-v", "threadtime", "*:" + priority], token, 10)).RequireSuccess();
        return FilterLogs(package, processes, logs);
    }
}
