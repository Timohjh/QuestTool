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
        var args = new List<string> { "-s", transport, "shell", "pm", "list", "packages" };
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
}
