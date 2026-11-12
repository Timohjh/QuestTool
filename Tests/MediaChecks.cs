using QuestTool.Core;

static class MediaChecks
{
    public static async Task RunAsync(Action<bool, string> check, string temp)
    {
        async Task Reject(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); } catch { rejected = true; }
            check(rejected, label);
        }
        var adb = new MediaAdb();
        var cli = new MediaCli();
        var service = new MediaService(adb, cli);
        var target = new DeploymentTarget("SERIAL", "USB", "");
        string castingDirectory = Path.Combine(temp, "Meta Casting", "client");
        string toolInfo = System.Text.Json.JsonSerializer.Serialize(new { installed = true, install_path = castingDirectory });
        check(MediaService.CastingDirectory(toolInfo) == castingDirectory, "Locate casting resources from CLI metadata instead of the project directory");
        await Reject(() => Task.FromResult(MediaService.CastingDirectory("{\"installed\":false,\"install_path\":null}")), "Missing casting resources report the install action");
        string destination = Path.Combine(temp, "video with spaces.mp4");

        adb.Serial = "OTHER";
        await Reject(() => service.StartRecordingAsync(target, destination, 1920, 1080, default), "Recording rejects a different headset identity");
        await Reject(() => service.StartStreamingAsync(target, default), "Streaming rejects a different headset identity");
        await Reject(() => service.SetProximityDisabledAsync(target, true, default), "Proximity toggle rejects a different headset identity");
        check(!cli.Commands.Any(c => c.Contains("start") || c.Contains("disable")), "Identity failures never start capture or change sensors");
        adb.Serial = "SERIAL";
        File.WriteAllText(destination, "keep");
        await Reject(() => service.StartRecordingAsync(target, destination, 1920, 1080, default), "Recording refuses to overwrite an existing file");
        check(File.ReadAllText(destination) == "keep", "Existing video remains unchanged");
        File.Delete(destination);
        await Reject(() => service.StartRecordingAsync(target, destination, 1921, 1080, default), "Recording validates encoder dimensions before starting");
        await service.StartRecordingAsync(target, destination, 1920, 1080, default);
        check(service.Recording?.Target.Serial == "SERIAL" && !File.Exists(destination), "Record start pins headset and reserves final publication until download");
        check(cli.Commands.Any(c => c.SequenceEqual(new[] { "--device", "USB", "--json", "capture", "video", "start", "--width", "1920", "--height", "1080" })), "Recording sends explicit device and resolution arguments");
        await Reject(() => service.StartRecordingAsync(target, destination, 1920, 1080, default), "Prevent a second recording while one is pending");
        cli.FailDownload = true;
        await Reject(() => service.StopRecordingAsync(default), "Report an interrupted video download");
        check(service.Recording is not null && !File.Exists(destination), "Interrupted download retains session and cannot publish a partial video");
        cli.FailDownload = false;
        check(await service.StopRecordingAsync(default) == destination && File.ReadAllText(destination) == "complete video", "Retry download and publish completed MP4 without overwriting");
        check(service.Recording is null && !Directory.EnumerateFiles(temp, ".questtool-recording-*").Any(), "Successful save clears session and staging file");

        string collision = Path.Combine(temp, "collision.mp4");
        await service.StartRecordingAsync(target, collision, 1280, 720, default);
        File.WriteAllText(collision, "another recording");
        await Reject(() => service.StopRecordingAsync(default), "Protect a destination created during recording");
        check(File.ReadAllText(collision) == "another recording" && service.Recording is not null, "Destination collision retains downloaded recording for recovery");
        int stops = cli.Commands.Count(c => c.Contains("--pull"));
        string recovered = Path.Combine(temp, "recovered.mp4");
        service.SetRecordingDestination(recovered);
        await service.StopRecordingAsync(default);
        check(File.ReadAllText(recovered) == "complete video" && stops == cli.Commands.Count(c => c.Contains("--pull")), "Recover a saved download under a new name without stopping the headset again");

        cli.Running = true;
        await Reject(() => service.StartStreamingAsync(target, default), "Do not replace an existing external casting window");
        check(service.StreamingTarget is null, "External casting window is never adopted for shutdown");
        cli.Running = false;
        await service.StartStreamingAsync(target, default);
        check(service.StreamingTarget?.Transport == "USB", "Live view remembers the verified transport");
        await service.StopStreamingAsync(default);
        check(service.StreamingTarget is null && !cli.Running, "Stop owned live view and clear its state");
        await service.StartStreamingAsync(target, default);
        cli.Running = false; // User closes Meta's window directly.
        check(!await service.RefreshStreamingAsync(default) && service.StreamingTarget is null, "Refresh detects an externally closed live-view window");
        cli.BadStatus = true;
        await Reject(() => service.RefreshStreamingAsync(default), "Unsupported casting status fails explicitly");
        cli.BadStatus = false;

        await service.SetProximityDisabledAsync(target, true, default);
        check(service.DisabledProximity.ContainsKey("SERIAL") && cli.Commands.Last().Last() == "disable", "Disable proximity and track headset for restoration");
        cli.Fail = true;
        await Reject(() => service.SetProximityDisabledAsync(target, false, default), "Surface a failed sensor restore");
        check(service.DisabledProximity.ContainsKey("SERIAL"), "Failed restore retains cleanup obligation");
        cli.Fail = false;
        await service.SetProximityDisabledAsync(target, false, default);
        check(service.DisabledProximity.Count == 0 && cli.Commands.Last().Last() == "enable", "Restore proximity and clear cleanup obligation");
        cli.JsonFailure = true;
        await Reject(() => service.SetProximityDisabledAsync(target, true, default), "Reject CLI JSON failure even with exit code zero");
        check(service.DisabledProximity.Count == 0, "Rejected sensor changes never appear successful");
        cli.JsonFailure = false;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        int count = cli.Commands.Count;
        await Reject(() => service.StartRecordingAsync(target, Path.Combine(temp, "cancelled.mp4"), 1024, 1024, cancel.Token), "Cancel recording before device mutation");
        check(cli.Commands.Count == count && service.Recording is null, "Cancelled start leaves no recording behind");
    }

    sealed class MediaAdb : IAdb
    {
        public string Serial = "SERIAL";
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = arguments.ToArray();
            return Task.FromResult(new CommandResult(0, a[0] == "connect" ? "connected to " + a[1] : "Meta\nQuest 3\n" + Serial));
        }
    }

    sealed class MediaCli : IAdb
    {
        public bool Running, Fail, FailDownload, JsonFailure, BadStatus;
        public List<string[]> Commands { get; } = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = arguments.ToArray(); Commands.Add(a);
            if (Fail) return Task.FromResult(new CommandResult(1, "Headset disconnected"));
            if (JsonFailure) return Task.FromResult(new CommandResult(0, "{\"success\":false,\"error\":\"not supported\"}"));
            if (a.Contains("casting"))
            {
                if (a.Last() == "status") return Task.FromResult(new CommandResult(0, BadStatus ? "not supported" : "{\"running\":" + (Running ? "true" : "false") + "}"));
                Running = a.Last() == "start";
            }
            if (a.Contains("--pull"))
            {
                File.WriteAllText(a.Last(), FailDownload ? "partial" : "complete video");
                if (FailDownload) return Task.FromResult(new CommandResult(1, "Download interrupted"));
            }
            return Task.FromResult(new CommandResult(0, "{\"success\":true}"));
        }
    }
}
