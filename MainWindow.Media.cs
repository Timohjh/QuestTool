using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuestTool.Core;

namespace QuestTool;

public partial class MainWindow
{
    MediaService? media;
    readonly Dictionary<string, bool> proximityRequests = [];
    bool finishingMedia;
    bool stoppingMirror;
    Process? mirrorProcess;
    bool MirrorRunning => mirrorProcess is not null && !mirrorProcess.HasExited;
    bool HasMediaState => MirrorRunning || media is not null && (media.Recording is not null || media.StreamingTarget is not null || media.DisabledProximity.Count > 0);

    MediaService Media()
    {
        if (!File.Exists(settings.MetaVrPath))
        {
            Tabs.SelectedItem = SetupTab;
            throw new IOException("Choose metavr.exe in Setup for video and proximity controls.");
        }
        return media ??= new(Adb, new AdbClient(settings.MetaVrPath, "Meta VR CLI"));
    }

    void UpdateMediaControls(bool busy)
    {
        busy |= stoppingMirror;
        bool session = media?.Recording is not null || media?.StreamingTarget is not null || MirrorRunning;
        MediaTargetControls.IsEnabled = !busy && !session;
        MetaVrControls.IsEnabled = !busy && !HasMediaState;
        AdbControls.IsEnabled = !busy && !HasMediaState;
        StreamStartButton.IsEnabled = !busy && media?.StreamingTarget is null && !MirrorRunning;
        StreamStopButton.IsEnabled = !busy && (media?.StreamingTarget is not null || MirrorRunning);
        MirrorSettingsPanel.IsEnabled = !busy && !session;
        StreamRefreshButton.IsEnabled = !busy;
        RecordStartButton.IsEnabled = !busy && media?.Recording is null;
        RecordStopButton.IsEnabled = !busy && media?.Recording is not null;
        RecordingSizeBox.IsEnabled = !busy && media?.Recording is null;
        ProximityOffBox.IsEnabled = !busy && MediaHeadsetBox.SelectedItem is Headset;
    }

    void MediaHeadsetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProximityOffBox is null) return;
        UpdateProximityDisplay();
        UpdateMediaControls(operation is not null || loadingFiles);
    }

    void UpdateProximityDisplay()
    {
        bool? requested = MediaHeadsetBox.SelectedItem is Headset h && proximityRequests.TryGetValue(h.Serial, out bool disabled) ? disabled : null;
        ProximityOffBox.IsChecked = requested;
        ProximityStatus.Text = requested switch
        {
            true => "Requested: sensor off. The headset stays awake when removed. Click again to restore.",
            false => "Requested: sensor on. Normal wear detection restored.",
            null => "Not changed by QuestTool in this session. Click to disable, then click again to restore."
        };
    }

    async Task RunMediaOperationAsync(string title, Func<MediaService, Headset, CancellationToken, Task> action)
    {
        if (MediaHeadsetBox.SelectedItem is not Headset headset) { Status("Choose a headset in Video first."); return; }
        await RunOperationAsync(title, async token =>
        {
            MediaStatus.Text = title;
            try { await action(Media(), headset, token); }
            catch (Exception ex) { MediaStatus.Text = ex.Message; throw; }
        });
    }

    async void StartStreamClick(object sender, RoutedEventArgs e)
    {
        if (StreamingBackendBox.SelectedIndex == 1)
        {
            if (operation is not null || loadingFiles || finishingMedia || stoppingMirror || MirrorRunning) return;
            try
            {
                SaveMirrorOptions();
                if (!File.Exists(settings.MirrorPath)) throw new IOException("Choose OculusMirror.exe in Setup. Meta Quest Link PC software is required.");
                mirrorProcess?.Dispose();
                mirrorProcess = null;
                mirrorProcess = Process.Start(settings.Mirror.StartInfo(settings.MirrorPath)) ?? throw new IOException("OculusMirror did not start.");
                StreamingStatus.Text = "OculusMirror launched. Start a Quest Link / Air Link PC-VR session to see its view; ADB selection does not select the Link headset.";
                MediaStatus.Text = StreamingStatus.Text;
                Status(MediaStatus.Text); Log(MediaStatus.Text);
                UpdateMediaControls(false);
            }
            catch (Exception ex) { MediaStatus.Text = ex.Message; Status(ex.Message); Log(ex.Message); }
            return;
        }
        await RunMediaOperationAsync("Starting live headset view…", async (service, headset, token) =>
        {
            await service.StartStreamingAsync(HeadsetTarget(headset, MediaConnectionBox.SelectedIndex), token);
            StreamingStatus.Text = $"Meta casting window launched · {headset.Name}. Use its gear button for resolution, bitrate, FPS, eye, and audio settings.";
            MediaStatus.Text = StreamingStatus.Text;
            Status(MediaStatus.Text); Log(MediaStatus.Text);
        });
    }

    async void StopStreamClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || loadingFiles || finishingMedia || stoppingMirror) return;
        if (MirrorRunning)
        {
            stoppingMirror = true;
            UpdateMediaControls(false);
            StreamingStatus.Text = MediaStatus.Text = "Stopping OculusMirror…";
            Status(MediaStatus.Text);
            try
            {
                await StopMirrorAsync();
                StreamingStatus.Text = MediaStatus.Text = "OculusMirror closed.";
                Status(MediaStatus.Text); Log(MediaStatus.Text);
            }
            catch (Exception ex)
            {
                StreamingStatus.Text = MediaStatus.Text = ex.Message;
                Status(ex.Message); Log(ex.Message);
            }
            finally
            {
                stoppingMirror = false;
                UpdateMediaControls(operation is not null || loadingFiles || finishingMedia);
            }
            return;
        }
        await RunMediaOperationAsync("Stopping live view…", async (service, _, token) =>
        {
            await service.StopStreamingAsync(token);
            StreamingStatus.Text = "Live view is stopped.";
            MediaStatus.Text = StreamingStatus.Text;
            Status(MediaStatus.Text); Log(MediaStatus.Text);
        });
    }

    async void RefreshStreamClick(object sender, RoutedEventArgs e) => await QueueRefreshAsync("Checking casting status…", ReadStreamingStatusAsync);

    async Task ReadStreamingStatusAsync(CancellationToken token)
    {
        try
        {
            if (StreamingBackendBox.SelectedIndex == 1)
            {
                StreamingStatus.Text = MirrorRunning ? "QuestTool's OculusMirror process is running. Its image requires an active Link session." : "QuestTool's OculusMirror is stopped.";
                Status(StreamingStatus.Text); return;
            }
            bool running = await Media().RefreshStreamingAsync(token);
            StreamingStatus.Text = running
                ? media?.StreamingTarget is not null ? "QuestTool's casting window is running." : "A casting window is running outside this session. Close it before starting another."
                : "Live view is stopped.";
            MediaStatus.Text = StreamingStatus.Text;
            Status(MediaStatus.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { MediaStatus.Text = ex.Message; throw; }
    }

    async void StartRecordingClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || loadingFiles || media?.Recording is not null) return;
        string folder = settings.RecordingFolder;
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var picker = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = $"QuestTool-{DateTime.Now:yyyyMMdd-HHmmss}.mp4", InitialDirectory = folder,
            Title = "Choose a new recording filename"
        };
        if (picker.ShowDialog(this) != true) return;
        settings.RecordingFolder = Path.GetDirectoryName(picker.FileName)!;
        await RunMediaOperationAsync("Starting video recording…", async (service, headset, token) =>
        {
            (int width, int height) = RecordingSizeBox.SelectedIndex switch { 1 => (1280, 720), 2 => (1024, 1024), _ => (1920, 1080) };
            await service.StartRecordingAsync(HeadsetTarget(headset, MediaConnectionBox.SelectedIndex), picker.FileName, width, height, token);
            RecordingStatus.Text = $"Recording started · {headset.Name}\nSave to: {picker.FileName}";
            MediaStatus.Text = "Recording started. Use Stop & save to finish and download.";
            Status(MediaStatus.Text); Log(RecordingStatus.Text);
        });
    }

    async Task SaveRecordingAsync(MediaService service, CancellationToken token)
    {
        string? saved = await service.StopRecordingAsync(token);
        if (saved is null) return;
        RecordingStatus.Text = "Saved: " + saved;
        MediaStatus.Text = RecordingStatus.Text;
        Status(MediaStatus.Text); Log(MediaStatus.Text);
    }

    async void StopRecordingClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || loadingFiles || finishingMedia) return;
        if (media?.Recording is { } recording && (File.Exists(recording.Destination) || Directory.Exists(recording.Destination)))
        {
            var picker = new SaveFileDialog { Filter = "MP4 video|*.mp4", DefaultExt = ".mp4", AddExtension = true,
                InitialDirectory = Path.GetDirectoryName(recording.Destination), Title = "Choose a new recording filename" };
            if (picker.ShowDialog(this) != true) return;
            try { media.SetRecordingDestination(picker.FileName); }
            catch (Exception ex) { MediaStatus.Text = ex.Message; Status(ex.Message); return; }
        }
        await RunMediaOperationAsync("Stopping and downloading video…", (service, _, token) => SaveRecordingAsync(service, token));
    }

    async void ProximityOffClick(object sender, RoutedEventArgs e)
    {
        if (MediaHeadsetBox.SelectedItem is not Headset headset) { UpdateProximityDisplay(); return; }
        bool disabled = !proximityRequests.GetValueOrDefault(headset.Serial);
        UpdateProximityDisplay(); // Reflect a new value only after a successful command.
        await RunMediaOperationAsync(disabled ? "Turning off proximity sensor…" : "Restoring proximity sensor…", async (service, _, token) =>
        {
            await service.SetProximityDisabledAsync(HeadsetTarget(headset, MediaConnectionBox.SelectedIndex), disabled, token);
            proximityRequests[headset.Serial] = disabled;
            UpdateProximityDisplay();
            MediaStatus.Text = headset.Name + (disabled ? ": proximity sensor turned off." : ": proximity sensor restored.");
            Status(MediaStatus.Text); Log(MediaStatus.Text);
        });
    }

    void OpenRecordingsClick(object sender, RoutedEventArgs e)
    {
        string folder = settings.RecordingFolder;
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { Status(ex.Message); }
    }

    void ChooseMetaVrClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Meta VR CLI|metavr.exe", Title = "Select Meta VR CLI" };
        if (picker.ShowDialog(this) != true) return;
        settings.MetaVrPath = picker.FileName;
        MetaVrPathBox.Text = settings.MetaVrPath;
        media = null;
        SaveSettings(); Status("Meta VR CLI configured. Click Check tools to verify media support.");
    }

    async void CheckMediaToolsClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Checking video tools…", async token =>
    {
        _ = Media();
        var cli = new AdbClient(settings.MetaVrPath, "Meta VR CLI");
        string version = (await cli.RunAsync(["--version"], token)).RequireSuccess();
        foreach (string[] command in new[] { new[] { "casting", "start", "--help" }, ["capture", "video", "start", "--help"], ["device", "proximity", "disable", "--help"] })
            (await cli.RunAsync(command, token)).RequireSuccess();
        string castingInfo = (await cli.RunAsync(["--json", "tools", "info", "casting"], token)).RequireSuccess();
        using var document = System.Text.Json.JsonDocument.Parse(castingInfo);
        bool installed = document.RootElement.TryGetProperty("installed", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
        MediaStatus.Text = version + (installed ? " · Casting client installed; recording and proximity commands available." : " · Casting client missing. Click Install / repair casting client.") + (File.Exists(settings.MirrorPath) ? " OculusMirror found." : " OculusMirror not found; choose its path for Link mirroring.");
        Status(MediaStatus.Text); Log(MediaStatus.Text);
    });

    async Task<bool> FinishMediaAsync()
    {
        if (MirrorRunning)
            try { await StopMirrorAsync(); }
            catch (Exception ex) { Status(ex.Message); return false; }
        if (media is null) return true;
        var errors = new List<string>();
        if (media.Recording is not null)
            try { await SaveRecordingAsync(media, CancellationToken.None); }
            catch (Exception ex) { errors.Add("Recording: " + ex.Message); }
        if (media.StreamingTarget is not null)
            try { await media.StopStreamingAsync(CancellationToken.None); StreamingStatus.Text = "Live view is stopped."; }
            catch (Exception ex) { errors.Add("Live view: " + ex.Message); }
        foreach (var target in media.DisabledProximity.Values.ToArray())
            try { await media.SetProximityDisabledAsync(target, false, CancellationToken.None); proximityRequests[target.Serial] = false; }
            catch (Exception ex) { errors.Add("Restore sensor " + target.Serial + ": " + ex.Message); }
        UpdateProximityDisplay();
        if (errors.Count == 0) return true;
        MediaStatus.Text = string.Join("\n", errors);
        Status("Could not finish video cleanup. Reconnect the headset, then retry closing. See Video and Activity log.");
        foreach (string error in errors) Log(error);
        Tabs.SelectedItem = MediaTab;
        return false;
    }

    void StreamingBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MirrorOptionsPanel is null) return;
        MirrorOptionsPanel.Visibility = StreamingBackendBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) { settings.StreamingBackend = StreamingBackendBox.SelectedIndex; SaveSettings(); }
    }

    void SaveMirrorOptions()
    {
        if (!int.TryParse(MirrorWidthBox.Text, out int width) || !int.TryParse(MirrorHeightBox.Text, out int height)
            || !double.TryParse(MirrorFovBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fov))
            throw new ArgumentException("Enter valid mirror dimensions and a FOV multiplier (for example 1.3).");
        var options = new MirrorOptions { Width = width, Height = height, Fov = fov, Eye = MirrorEyeBox.SelectedIndex switch { 0 => "Left", 2 => "Both", _ => "Right" }, Guardian = MirrorGuardianBox.IsChecked == true, Notifications = MirrorNotificationsBox.IsChecked == true, SystemGui = MirrorGuiBox.IsChecked == true };
        _ = options.StartInfo(settings.MirrorPath.Length > 0 ? settings.MirrorPath : "OculusMirror.exe");
        settings.Mirror = options;
        SaveSettings();
    }

    async Task StopMirrorAsync()
    {
        var process = mirrorProcess;
        if (process is null) return;
        await MirrorOptions.StopAsync(process);
        process.Dispose(); mirrorProcess = null;
    }

    void ChooseMirrorClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Oculus Mirror|OculusMirror.exe" };
        if (picker.ShowDialog(this) != true) return;
        settings.MirrorPath = picker.FileName; MirrorPathBox.Text = picker.FileName; SaveSettings();
    }

    async void InstallCastingClick(object sender, RoutedEventArgs e) => await RunOperationAsync("Installing Meta casting client…", async token =>
    {
        _ = Media();
        string result = (await new AdbClient(settings.MetaVrPath, "Meta VR CLI").RunAsync(["tools", "install", "casting", "--force"], token, 600)).RequireSuccess();
        Log(result); MediaStatus.Text = "Meta casting client installed. Start live view in Video."; Status(MediaStatus.Text);
    });
}
