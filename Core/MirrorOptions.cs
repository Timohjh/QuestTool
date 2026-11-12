using System.Diagnostics;
using System.Globalization;

namespace QuestTool.Core;

public sealed class MirrorOptions
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string Eye { get; set; } = "Right";
    public double Fov { get; set; } = 1.0;
    public bool Guardian { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool SystemGui { get; set; } = true;

    public ProcessStartInfo StartInfo(string path)
    {
        if (Width is < 256 or > 7680 || Height is < 256 or > 4320 || !double.IsFinite(Fov) || Fov is < 0.5 or > 1.9)
            throw new ArgumentException("Mirror size must be 256–7680 × 256–4320; FOV multiplier must be 0.5–1.9.");
        string eye = Eye switch { "Left" => "--LeftEyeOnly", "Right" => "--RightEyeOnly", "Both" => "--RectilinearBothEyes", _ => throw new ArgumentException("Choose Left, Right, or Both eyes.") };
        var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
        foreach (string arg in new[] { "--Size", Width.ToString(CultureInfo.InvariantCulture), Height.ToString(CultureInfo.InvariantCulture), eye, "--FovTanAngleMultiplier", Fov.ToString(CultureInfo.InvariantCulture), Fov.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
        if (Guardian) info.ArgumentList.Add("--IncludeGuardian");
        if (Notifications) info.ArgumentList.Add("--IncludeNotifications");
        if (SystemGui) info.ArgumentList.Add("--IncludeSystemGui");
        return info;
    }

    public static string Find()
    {
        string program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[] { "Meta Horizon", "Oculus" }.Select(folder => Path.Combine(program, folder, "Support", "oculus-diagnostics", "OculusMirror.exe")).FirstOrDefault(File.Exists) ?? "";
    }
}
