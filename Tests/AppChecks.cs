using QuestTool.Core;

static class AppChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var apps = AppService.ParseList("package:com.test.Zed\r\npackage:com.test.Alpha\npackage:com.test.Zed\nnoise\npackage:bad;reboot");
        check(apps.Count == 2 && apps[0].Name == "Alpha", "Parse, sort and deduplicate app list; reject malformed package names");
        string details = AppService.ParseDetails("com.test.Alpha", "Package [com.test.Alpha] (abc):\n  versionCode=42 minSdk=29\n  versionName=1.2\n  firstInstallTime=2026-01-01 10:00:00\n  lastUpdateTime=2026-02-01 10:00:00\n  codePath=/data/app/alpha\n  dataDir=/data/user/0/com.test.Alpha");
        check(details.Contains("Version: 1.2\nVersion code: 42") && details.Contains("Last updated: 2026-02-01") && details.Contains("APK path: /data/app/alpha"), "Read version, dates and installation paths from package details");
        bool rejected = false;
        try { AppService.ParseDetails("com.test.Alpha", "Unable to find package"); } catch (IOException) { rejected = true; }
        check(rejected, "Removed apps report unavailable details");

        var fake = new AppAdb();
        var service = new AppService(fake);
        var target = new DeploymentTarget("SERIAL", "USB", "");
        await service.ListAsync(target, false, default);
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "pm", "list", "packages", "-3" }), "Default app list excludes system packages on verified headset");
        await service.ListAsync(target, true, default);
        check(fake.Commands.Last().Last() == "packages", "System app option lists all installed packages");
        await service.LaunchAsync(target, "com.test.Alpha", default);
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "am", "start", "-W", "-n", "'com.test.Alpha/.MainActivity'" }), "Launch resolved activity and wait for Android confirmation");
        fake.UseInfo = true;
        await service.LaunchAsync(target, "com.test.Alpha", default);
        check(fake.Commands.Any(command => command.Contains("android.intent.category.INFO")), "Resolve INFO activity for VR apps without LAUNCHER activity");
        fake.LaunchResult = "Error: Activity not started";
        rejected = false;
        try { await service.LaunchAsync(target, "com.test.Alpha", default); } catch (IOException) { rejected = true; }
        check(rejected, "Do not report failed launches as successful when shell exit code is zero");
        await service.CloseAsync(target, "com.test.Alpha", default);
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "am", "force-stop", "com.test.Alpha" }), "Close only selected package on verified device");
        fake.NoActivity = true;
        int starts = fake.Commands.Count(command => command.Contains("start"));
        rejected = false;
        try { await service.LaunchAsync(target, "com.test.Alpha", default); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && starts == fake.Commands.Count(command => command.Contains("start")), "Packages without an activity cannot be launched");
        int before = fake.Commands.Count;
        rejected = false;
        try { await service.CloseAsync(target, "com.test.Alpha;reboot", default); } catch (ArgumentException) { rejected = true; }
        check(rejected && fake.Commands.Count == before, "Reject shell metacharacters before any device command");
        fake.Serial = "OTHER";
        rejected = false;
        try { await service.CloseAsync(target, "com.test.Alpha", default); } catch (IOException) { rejected = true; }
        check(rejected && fake.Commands.Count(command => command.Contains("force-stop")) == 1, "Wrong headset identity blocks app controls");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        rejected = false;
        try { await service.ListAsync(target, false, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
        check(rejected, "App reads support cancellation");
    }

    sealed class AppAdb : IAdb
    {
        public string Serial = "SERIAL", LaunchResult = "Starting: Intent\nStatus: ok\nComplete";
        public bool UseInfo, NoActivity;
        public List<string[]> Commands = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = arguments.ToArray(); Commands.Add(a);
            string output = a.Last() == DeviceService.IdentityCommand ? "Meta\nQuest 3\n" + Serial
                : a.Contains("resolve-activity") ? NoActivity || (UseInfo && a.Contains("android.intent.category.LAUNCHER")) ? "No activity found" : "com.test.Alpha/.MainActivity"
                : a.Contains("start") ? LaunchResult : a.Contains("packages") ? "package:com.test.Alpha" : "";
            return Task.FromResult(new CommandResult(0, output));
        }
    }
}
