using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace QuestTool.Core;

public sealed record DeploymentTarget(string Serial, string Transport, string Endpoint);
public sealed record DeploymentUpdate(string Serial, string Message, bool Active, string? Version = null);

public sealed class DeploymentService(IAdb adb)
{
    readonly SemaphoreSlim transfers = new(2);
    readonly ConcurrentDictionary<string, Lazy<Task<string>>> obbHashes = new(StringComparer.OrdinalIgnoreCase);

    static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
    public static long? ParseVersion(string dump)
    {
        var match = Regex.Match(dump, @"\bversionCode=(\d+)");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out long version)) return null;
        var major = Regex.Match(dump, @"\bversionCodeMajor=(\d+)");
        if (major.Success && uint.TryParse(major.Groups[1].Value, out uint high)) return ((long)high << 32) | (uint)version;
        return version;
    }

    public async Task DeployAsync(DeploymentTarget target, ApkInfo apk, IReadOnlyList<string> obbs, bool waitForOffline, IProgress<DeploymentUpdate> progress, CancellationToken token)
    {
        bool installed = false;
        bool transferSlot = false;
        string transport = "";
        string? staging = null;
        void Report(string message, bool active = true, string? version = null) => progress.Report(new(target.Serial, message, active, version));
        async Task<string> Run(params string[] args) => (await adb.RunAsync(new[] { "-s", transport }.Concat(args), token, args[0] == "push" ? 3600 : args[0] == "install" ? 900 : args.Contains("sha256sum") ? 600 : 30)).RequireSuccess();
        try
        {
            apk.ValidateObbs(obbs);
            Report("Connecting…");
            var devices = new DeviceService(adb);
            var deadline = DateTime.UtcNow.AddMinutes(30);
            while (true)
            {
                try { transport = await devices.ResolveAsync(target, token); break; }
                catch (IOException) when (waitForOffline && DateTime.UtcNow < deadline)
                {
                    Report("Waiting for headset · up to 30 minutes");
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
            Report("Waiting for transfer slot…");
            await transfers.WaitAsync(token);
            transferSlot = true;
            // A queued transport may have disconnected or changed identity in the meantime.
            transport = await devices.ResolveAsync(target, token);
            Report("Checking installed version…");
            var current = ParseVersion(await Run("shell", "dumpsys", "package", apk.Package));
            if (current > apk.VersionCode) throw new InvalidOperationException($"Installed version {current} is newer than {apk.VersionCode}. Downgrade blocked.");
            // Check both destinations conservatively; Android may use the same underlying volume.
            long bytes = new FileInfo(apk.Path).Length * 2 + obbs.Sum(p => new FileInfo(p).Length) + 128L * 1024 * 1024;
            await CheckSpaceAsync("/data", bytes);
            if (obbs.Count > 0) await CheckSpaceAsync("/sdcard", bytes);
            Report("Installing APK…");
            string install = await Run("install", "-r", apk.Path);
            if (!Regex.IsMatch(install, @"(?m)^Success\s*$")) throw new InvalidOperationException("Installer did not confirm success: " + install);
            installed = true;
            if (obbs.Count > 0)
            {
                // Stop the updated app before replacing its expansion files.
                await Run("shell", "am", "force-stop", apk.Package);
                string directory = apk.ObbDirectory;
                await Run("shell", "mkdir", "-p", directory);
                foreach (var obb in obbs)
                {
                    string destination = directory + "/" + Path.GetFileName(obb);
                    Report("Checking source OBB · " + Path.GetFileName(obb));
                    string expectedHash = await obbHashes.GetOrAdd(obb, path => new(() => HashFileAsync(path, token))).Value;
                    staging = destination + ".questtool-" + Guid.NewGuid().ToString("N") + ".part";
                    Report("Copying OBB → " + destination);
                    await Run("push", obb, staging);
                    string size = await Run("shell", "stat", "-c", "%s", staging);
                    if (!long.TryParse(size.Trim(), out long actual) || actual != new FileInfo(obb).Length) throw new IOException("OBB size verification failed. Existing OBB was not replaced.");
                    Report("Verifying OBB SHA-256 · " + Path.GetFileName(obb));
                    string hash = (await Run("shell", "sha256sum", staging)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("OBB checksum verification failed. Existing OBB was not replaced.");
                    await Run("shell", "mv", "-f", staging, destination);
                    staging = null;
                    string finalSize = await Run("shell", "stat", "-c", "%s", destination);
                    if (!long.TryParse(finalSize.Trim(), out long stored) || stored != actual) throw new IOException("OBB not found with the expected size in its final folder: " + destination);
                }
            }
            Report("Verifying installed version…");
            var verified = ParseVersion(await Run("shell", "dumpsys", "package", apk.Package));
            if (verified != apk.VersionCode) throw new InvalidOperationException($"Expected version {apk.VersionCode}, got {verified?.ToString() ?? "unknown"}.");
            Report("Updated · verified", false, verified.ToString());

            async Task CheckSpaceAsync(string location, long required)
            {
                string disk = await Run("shell", "df", "-k", location);
                var lines = disk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var columns = lines.LastOrDefault()?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns is null || columns.Length < 4 || !long.TryParse(columns[3], out long available)) throw new IOException("Cannot read free space at " + location + ".");
                if (available < (required + 1023) / 1024) throw new IOException($"Not enough free storage at {location}. Need about {required / 1024 / 1024} MB.");
            }
        }
        catch (OperationCanceledException) { Report(installed ? "Cancelled · APK installed; check OBBs" : "Cancelled · verify device before retry", false); }
        catch (Exception ex) { Report((installed ? "Partial update · APK installed: " : "Failed · ") + ex.Message, false); }
        finally
        {
            if (transferSlot) transfers.Release();
            if (staging is not null && transport.Length > 0)
            {
                // Delete only the exact temporary path created by this operation.
                try { await adb.RunAsync(["-s", transport, "shell", "rm", "-f", staging], CancellationToken.None, 5); }
                catch (Exception) { /* A disconnected headset can retain this .part file. */ }
            }
        }
    }
}
