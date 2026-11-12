using System.Diagnostics;

namespace QuestTool.Core;

public sealed class AdbClient(string path, string toolName = "ADB", string? workingDirectory = null) : IAdb
{
    public AdbClient InDirectory(string directory) => new(path, toolName, directory);

    public async Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        using var process = new Process { StartInfo = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (workingDirectory is not null) process.StartInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                if (cancellation.IsCancellationRequested) throw;
                throw new TimeoutException($"{toolName} timed out after {timeoutSeconds}s. Check the headset connection.");
            }
            return new(process.ExitCode, ((await stdout) + "\n" + (await stderr)).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Cannot start {toolName}. Choose its executable in Setup. " + ex.Message, ex);
        }
    }

    public static string Find()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(program, "Meta VR Developer Hub", "resources", "bin", "adb.exe"),
            Path.Combine(program, "Meta Quest Developer Hub", "resources", "bin", "adb.exe"),
            Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe")
        };
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk) candidates.Add(Path.Combine(sdk, "platform-tools", "adb.exe"));
        foreach (var part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(part)) candidates.Add(Path.Combine(part.Trim('"'), "adb.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
