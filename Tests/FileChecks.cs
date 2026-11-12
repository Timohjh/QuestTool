using QuestTool.Core;

static class FileChecks
{
    public static async Task RunAsync(Action<bool, string> check, string temp)
    {
        async Task Reject(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); } catch (Exception) { rejected = true; }
            check(rejected, label);
        }
        check(FileService.NormalizePath("/sdcard/Download/../Movies//") == "/sdcard/Movies", "Normalize shared-storage paths");
        var adb = new FileAdb(); var service = new FileService(adb);
        var target = new DeploymentTarget("SERIAL", "USB", "");
        foreach (string path in new[] { "/", "/data", "/sdcard/../data", "/sdcard/Download\n/data", "sdcard/Download" })
            await Reject(() => service.ListAsync(target, path, default), "Reject file-browser path " + path.Replace('\n', ' '));
        await Reject(() => service.CreateFolderAsync(target, "/sdcard", "../data", default), "Reject folder-name traversal");
        await Reject(() => service.DeleteAsync(target, "/sdcard", default), "Prevent deleting shared-storage root");
        await Reject(() => service.MoveAsync(target, "/sdcard/Folder", "/sdcard/Folder/Inside", default), "Prevent moving a folder inside itself");
        check(adb.Commands.Count == 0, "Invalid file operations never reach ADB");
        adb.Listing = "-rw-rw----\0" + "12\0" + "1762876800.123456789\0a file's name.txt\0drwxrwx---\0" + "4096\0" + "1762790400.000000000\0Empty\0-rw-rw----\0" + "0\0" + "1762704000.000000000\0.hidden\0";
        var entries = await service.ListAsync(target, "/sdcard", default);
        check(entries.Count == 3 && entries[0].IsDirectory && entries.Any(f => f.Name == "a file's name.txt" && f.Size == 12) && entries.Any(f => f.Name == ".hidden"), "Read folders, hidden files, spaces and apostrophes without splitting names");
        check(entries.Single(f => f.Name == "a file's name.txt").Modified?.ToUnixTimeSeconds() == 1762876800 && entries.All(f => f.ModifiedText.Length == 16), "Read fractional modification timestamps and format dates for the file list");
        adb.Serial = "OTHER";
        await Reject(() => service.DeleteAsync(target, "/sdcard/test.txt", default), "Refuse file changes on a different headset");
        check(!adb.Commands.Any(c => c.Last().Contains("rm -r --")), "Identity mismatch sends no delete command");
        adb.Serial = "SERIAL";
        string file = Path.Combine(temp, "upload check.txt"); File.WriteAllText(file, "original");
        adb.FailPush = true;
        await Reject(() => service.UploadAsync(target, "/sdcard/Download", file, true, default), "Report failed file upload");
        check(!adb.Commands.Any(c => c.Last().Contains("mv -fT")), "Failed upload cannot replace the destination");
        check(adb.Commands.Any(c => c.Last().Contains("rm -rf -- \"$stage\"")), "Failed upload cleans its unique staging item");
        string existing = Path.Combine(temp, "existing.txt"); File.WriteAllText(existing, "keep");
        await Reject(() => service.DownloadAsync(target, new("test.txt", "/sdcard/test.txt", "File", 1), existing, default), "Downloads preserve an existing local file");
        check(File.ReadAllText(existing) == "keep" && !adb.Commands.Any(c => c.Contains("pull")), "Existing local destination remains unchanged");
        var updates = new List<FileTransferProgress>();
        string received = Path.Combine(temp, "received.bin");
        await service.DownloadAsync(target, new("test.bin", "/sdcard/test.bin", "File", 4), received, default, new InlineProgress<FileTransferProgress>(updates.Add));
        check(File.ReadAllBytes(received).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && updates.Count > 0 && updates[^1].Bytes == 4 && updates[^1].TotalBytes == 4, "Download reports byte progress and retains the completed file");
        adb.FailPull = true;
        string failed = Path.Combine(temp, "failed.bin");
        await Reject(() => service.DownloadAsync(target, new("test.bin", "/sdcard/test.bin", "File", 4), failed, default), "Surface download transfer failures");
        check(!File.Exists(failed) && !Directory.EnumerateFileSystemEntries(temp, ".questtool-download-*").Any(), "Failed download removes partial staging data");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Reject(() => service.ListAsync(target, "/sdcard", cancellation.Token), "Cancel file browsing promptly");
    }

    public static async Task HardwareAsync(string adbPath, string serial, string transport)
    {
        int passed = 0;
        string connection = DeviceService.IsEndpoint(transport) ? "Wi-Fi" : "USB";
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); Console.WriteLine("PASS " + label.Replace("Wi-Fi:", connection + ":")); passed++; }
        async Task Reject(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); } catch (Exception) { rejected = true; }
            Check(rejected, label);
        }
        var service = new FileService(new AdbClient(adbPath));
        var target = new DeploymentTarget(serial, transport, DeviceService.IsEndpoint(transport) ? transport : "");
        string name = "QuestTool-check-" + Guid.NewGuid().ToString("N");
        string remote = FileService.ChildPath("/sdcard/Download", name);
        string local = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(local);
        bool created = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        try
        {
            await service.CreateFolderAsync(target, "/sdcard/Download", name, token); created = true;
            Check((await service.ListAsync(target, remote, token)).Count == 0, "Wi-Fi: create and browse an empty folder");
            string fileName = "space ' & $(echo nope) ü.txt";
            string source = Path.Combine(local, fileName); File.WriteAllBytes(source, System.Security.Cryptography.RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            await service.UploadAsync(target, remote, source, false, token);
            var item = (await service.ListAsync(target, remote, token)).Single();
            Check(item.Name == fileName && item.Size == new FileInfo(source).Length && item.Modified is not null, "Wi-Fi: list file name, size and modification date after upload");
            string download = Path.Combine(local, "download.txt");
            var updates = new List<FileTransferProgress>();
            await service.DownloadAsync(target, item, download, token, new InlineProgress<FileTransferProgress>(updates.Add));
            Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(download)), "Wi-Fi: download bytes match the original");
            Check(updates.Count > 0 && updates[^1].Bytes == item.Size, "Wi-Fi: download progress reaches the complete file size");
            var completed = updates[^1];
            Console.WriteLine($"{connection} transfer: {completed.Bytes / 1048576d:N1} MB in {completed.Elapsed.TotalSeconds:N2}s ({completed.Bytes / 1048576d / completed.Elapsed.TotalSeconds:N1} MB/s).");
            await Reject(() => service.UploadAsync(target, remote, source, false, token), "Wi-Fi: prevent unapproved replacement");
            File.WriteAllText(source, "Updated contents");
            await service.UploadAsync(target, remote, source, true, token);
            await service.DownloadAsync(target, item, Path.Combine(local, "updated.txt"), token);
            Check(File.ReadAllText(Path.Combine(local, "updated.txt")) == "Updated contents", "Wi-Fi: replace an existing file after a successful staged upload");
            await service.CreateFolderAsync(target, remote, "New folder", token);
            string moved = remote + "/New folder/renamed.txt";
            await service.MoveAsync(target, item.Path, moved, token);
            Check((await service.ListAsync(target, remote + "/New folder", token)).Single().Name == "renamed.txt", "Wi-Fi: move and rename a file");
            await service.DeleteAsync(target, moved, token);
            Check((await service.ListAsync(target, remote + "/New folder", token)).Count == 0, "Wi-Fi: delete a file");
            string folder = Path.Combine(local, "Folder ü with spaces");
            Directory.CreateDirectory(Path.Combine(folder, "Nested folder"));
            File.WriteAllText(Path.Combine(folder, ".hidden"), "hidden");
            File.WriteAllText(Path.Combine(folder, "Nested folder", "nested.txt"), "nested contents");
            await service.UploadAsync(target, remote, folder, false, token);
            var folderEntry = (await service.ListAsync(target, remote, token)).Single(f => f.Name == Path.GetFileName(folder));
            Check(folderEntry.IsDirectory && (await service.ListAsync(target, folderEntry.Path, token)).Any(f => f.Name == ".hidden"), "Wi-Fi: upload a nested folder and list hidden files");
            string localFolder = Path.Combine(local, "downloaded-folder");
            await service.DownloadAsync(target, folderEntry, localFolder, token);
            Check(File.ReadAllText(Path.Combine(localFolder, "Nested folder", "nested.txt")) == "nested contents" && File.ReadAllText(Path.Combine(localFolder, ".hidden")) == "hidden", "Wi-Fi: download a folder with its contents");
            await Reject(() => service.DownloadAsync(target, folderEntry, localFolder, token), "Wi-Fi: preserve an existing local folder");
            await service.MoveAsync(target, folderEntry.Path, remote + "/New folder/Moved folder", token);
            Check((await service.ListAsync(target, remote + "/New folder", token)).Single().Name == "Moved folder", "Wi-Fi: move a folder with its contents");
            await service.DeleteAsync(target, remote + "/New folder", token);
            Check((await service.ListAsync(target, remote, token)).Count == 0, "Wi-Fi: delete a folder with its contents");
            Console.WriteLine($"{passed} hardware checks passed over {transport}.");
        }
        finally
        {
            try { if (created) await service.DeleteAsync(target, remote, CancellationToken.None); }
            finally { Directory.Delete(local, true); }
        }
    }

    sealed class FileAdb : IAdb
    {
        public string Serial = "SERIAL", Listing = "";
        public bool FailPush, FailPull;
        public List<string[]> Commands = [];
        public Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int timeoutSeconds = 30)
        {
            cancellation.ThrowIfCancellationRequested();
            string[] a = arguments.ToArray(); Commands.Add(a);
            if (a.Last() == DeviceService.IdentityCommand) return Task.FromResult(new CommandResult(0, "Meta\nQuest 3\n" + Serial));
            if (a.Contains("getprop")) return Task.FromResult(new CommandResult(0, a.Last() switch { "ro.product.manufacturer" => "Meta", "ro.product.model" => "Quest 3", _ => Serial }));
            if (a.Contains("push") && FailPush) return Task.FromResult(new CommandResult(1, "adb: error: connection lost"));
            if (a.Contains("pull")) return PullAsync(a.Last(), cancellation);
            return Task.FromResult(new CommandResult(0, a.Last().Contains("find . -mindepth") ? Listing : ""));
        }

        async Task<CommandResult> PullAsync(string destination, CancellationToken cancellation)
        {
            await File.WriteAllBytesAsync(destination, [1, 2, 3, 4], cancellation);
            await Task.Delay(40, cancellation);
            return new(FailPull ? 1 : 0, FailPull ? "adb: error: connection lost" : "1 file pulled");
        }
    }
}
