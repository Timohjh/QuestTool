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
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "pm", "list", "packages", "--user", "current", "-3" }), "Default app list excludes system packages on verified headset for current user");
        await service.ListAsync(target, true, default);
        check(fake.Commands.Last().Contains("packages") && !fake.Commands.Last().Contains("-3"), "System app option lists all installed packages");
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
        fake.Serial = "SERIAL"; fake.NoActivity = false; fake.LaunchResult = "Status: ok";
        before = fake.Commands.Count;
        await service.RestartAsync(target, "com.test.Alpha", default);
        var restart = fake.Commands.Skip(before).ToArray();
        check(Array.FindIndex(restart, a => a.Contains("force-stop")) < Array.FindIndex(restart, a => a.Contains("start")), "Restart closes app before launching it");
        fake.CloseResult = "SecurityException: denied";
        starts = fake.Commands.Count(a => a.Contains("start"));
        rejected = false;
        try { await service.RestartAsync(target, "com.test.Alpha", default); } catch (IOException) { rejected = true; }
        check(rejected && starts == fake.Commands.Count(a => a.Contains("start")), "Restart does not launch when close fails");
        fake.CloseResult = "";
        await service.UninstallAsync(target, "com.test.Alpha", default);
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "pm", "uninstall", "--user", "current", "com.test.Alpha" }), "Uninstall targets the chosen app for the current headset user");
        await service.ClearAsync(target, "com.test.Alpha", false, default);
        check(fake.Commands.Last().SequenceEqual(new[] { "-s", "USB", "shell", "pm", "clear", "--user", "current", "com.test.Alpha" }), "Clear app data targets a single package and user");
        await service.ClearAsync(target, "com.test.Alpha", true, default);
        check(fake.Commands.Last().Contains("--cache-only"), "Cache clearing uses cache-only flag even when help exits with code 1");
        fake.CacheSupported = false;
        int clears = fake.Commands.Count(a => a.Contains("clear"));
        rejected = false;
        try { await service.ClearAsync(target, "com.test.Alpha", true, default); } catch (NotSupportedException) { rejected = true; }
        check(rejected && clears == fake.Commands.Count(a => a.Contains("clear")), "Unsupported cache clearing never falls back to clearing saved data");
        fake.MutationResult = "Failure [DELETE_FAILED_INTERNAL_ERROR]";
        rejected = false;
        try { await service.UninstallAsync(target, "com.test.Alpha", default); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Uninstall failure is reported instead of removing the app row");
        fake.MutationResult = "not confirmed";
        rejected = false;
        try { await service.ClearAsync(target, "com.test.Alpha", false, default); } catch (IOException) { rejected = true; }
        check(rejected, "Data clearing requires an explicit Success response");
        await service.OpenSettingsAsync(target, "com.test.Alpha", default);
        check(fake.Commands.Last().Contains("android.settings.APPLICATION_DETAILS_SETTINGS") && fake.Commands.Last().Last() == "package:com.test.Alpha", "Open settings for the selected package");
        fake.LaunchResult = "Error: Activity not found";
        rejected = false;
        try { await service.OpenSettingsAsync(target, "com.test.Alpha", default); } catch (IOException) { rejected = true; }
        check(rejected, "Unavailable headset app settings report an error");

        string processes = "PID NAME\n123 com.test.Alpha\n124 com.test.Alpha:worker\n125 com.test.Alphabet\n126 unrelated";
        string logs = "11-13 10:00:00.000  123  123 I Unity: main\n11-13 10:00:00.001  124  124 I Worker: child\n11-13 10:00:00.002  125  125 I Other: excluded\n11-13 10:00:00.003  126  126 I Other: com.test.Alpha";
        string filtered = AppService.FilterLogs("com.test.Alpha", processes, logs);
        check(filtered.Contains("main") && filtered.Contains("child") && !filtered.Contains("excluded") && !filtered.Contains("Other"), "App logs include main and child processes without leaking other apps' lines");
        rejected = false;
        try { AppService.FilterLogs("com.test.Alpha", "PID NAME\n125 com.test.Alphabet", logs); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Stopped apps report no running processes instead of showing all device logs");
        fake.Processes = processes; fake.Logs = logs;
        check(await service.ReadLogsAsync(target, "com.test.Alpha", 'I', default) == filtered, "App log service applies process filtering to bounded device capture");
        var paths = AppService.ParseApkPaths("package:/data/app/alpha/base.apk\r\npackage:/data/app/alpha/split_config.arm64.apk");
        check(paths.Count == 2, "Discover both base and split APKs");
        rejected = false;
        try { AppService.ParseApkPaths("package:/data/app/../secret.apk"); } catch (IOException) { rejected = true; }
        check(rejected, "Reject malformed APK paths before export");
        string temp = Path.Combine(Path.GetTempPath(), "QuestTool-app-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string exported = await service.ExportAsync(target, "com.test.Alpha", temp, null, default);
            check(Directory.GetFiles(exported, "*.apk").Length == 2 && Directory.GetFiles(exported).All(file => new FileInfo(file).Length > 0), "Export all APKs into a unique completed folder");
            string existing = Path.Combine(temp, "keep.txt"); await File.WriteAllTextAsync(existing, "keep");
            fake.FailPull = true;
            rejected = false;
            try { await service.ExportAsync(target, "com.test.Alpha", temp, null, default); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && !Directory.EnumerateDirectories(temp, ".questtool-export-*").Any() && File.ReadAllText(existing) == "keep" && Directory.Exists(exported), "Failed export removes its partial files and preserves existing local files");
        }
        finally { Directory.Delete(temp, true); }

        fake.Serial = "OTHER";
        before = fake.Commands.Count;
        foreach (var action in new Func<Task>[] {
            () => service.UninstallAsync(target, "com.test.Alpha", default),
            () => service.ClearAsync(target, "com.test.Alpha", false, default),
            () => service.OpenSettingsAsync(target, "com.test.Alpha", default),
            async () => { await service.ReadLogsAsync(target, "com.test.Alpha", 'I', default); }
        })
        {
            rejected = false;
            try { await action(); } catch (IOException) { rejected = true; }
            check(rejected, "Additional app action rejects a mismatched headset");
        }
        check(fake.Commands.Skip(before).All(a => a.Last() == DeviceService.IdentityCommand), "Mismatched headset receives no app commands");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        rejected = false;
        try { await service.ListAsync(target, false, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
        check(rejected, "App reads support cancellation");
    }

    sealed class AppAdb : IAdb
    {
        public string Serial = "SERIAL", LaunchResult = "Starting: Intent\nStatus: ok\nComplete", CloseResult = "", MutationResult = "Success", Processes = "", Logs = "";
        public bool UseInfo, NoActivity, CacheSupported = true, FailPull;
        public List<string[]> Commands = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = arguments.ToArray(); Commands.Add(a);
            if (a.Contains("pull"))
            {
                File.WriteAllBytes(a.Last(), [0x50, 0x4b, 3, 4]);
                return Task.FromResult(new CommandResult(FailPull ? 1 : 0, FailPull ? "Transfer failed" : "1 file pulled"));
            }
            if (a.Last() == "help") return Task.FromResult(new CommandResult(1, CacheSupported ? "clear [--cache-only] PACKAGE" : "clear PACKAGE"));
            string output = a.Last() == DeviceService.IdentityCommand ? "Meta\nQuest 3\n" + Serial
                : a.Contains("resolve-activity") ? NoActivity || (UseInfo && a.Contains("android.intent.category.LAUNCHER")) ? "No activity found" : "com.test.Alpha/.MainActivity"
                : a.Contains("start") ? LaunchResult : a.Contains("packages") ? "package:com.test.Alpha"
                : a.Contains("force-stop") ? CloseResult : a.Contains("uninstall") || a.Contains("clear") ? MutationResult
                : a.Contains("path") ? "package:/data/app/alpha/base.apk\npackage:/data/app/alpha/split_config.arm64.apk"
                : a.Contains("ps") ? Processes : a.Contains("logcat") ? Logs : "";
            return Task.FromResult(new CommandResult(0, output));
        }
    }
}
