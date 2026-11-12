using System.Globalization;
using System.Diagnostics;

namespace QuestTool.Core;

public sealed record HeadsetFile(string Name, string Path, string Kind, long Size, DateTimeOffset? Modified = null)
{
    public bool IsDirectory => Kind == "Folder";
    public bool IsLink => Kind == "Link";
    public string SizeText => Kind != "File" ? "" : Size < 1024 ? $"{Size:N0} B" : Size < 1048576 ? $"{Size / 1024d:N1} KB" : Size < 1073741824 ? $"{Size / 1048576d:N1} MB" : $"{Size / 1073741824d:N1} GB";
    public string ModifiedText => Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
}

public sealed record FileTransferProgress(long Bytes, long TotalBytes, TimeSpan Elapsed);

public sealed class FileService(IAdb adb)
{
    public const string Root = "/sdcard";

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Contains('\\'))
            throw new ArgumentException("Enter a headset folder under /sdcard.");
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count <= 1) throw new ArgumentException("Stay inside shared storage (/sdcard).");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        if (!path.StartsWith('/') || parts.Count == 0 || parts[0] != "sdcard")
            throw new ArgumentException("Choose a folder inside shared storage (/sdcard).");
        return "/" + string.Join('/', parts);
    }

    public static string ChildPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Use a file or folder name without slashes or control characters.");
        return NormalizePath(directory) + "/" + name;
    }

    public static string ParentPath(string path)
    {
        path = NormalizePath(path);
        return path == Root ? Root : path[..path.LastIndexOf('/')];
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    static string DirectoryGuard(string path) => $$"""
        set -e
        root=$(readlink -f /sdcard)
        dir=$(readlink -f {{Quote(NormalizePath(path))}})
        [ -n "$root" ] && [ -n "$dir" ] || { echo 'Cannot resolve shared storage folder.' >&2; exit 1; }
        case "$dir" in "$root"|"$root"/*) ;; *) echo 'Folder points outside shared storage.' >&2; exit 1;; esac
        [ -d "$dir" ] && [ -r "$dir" ] || { echo 'Folder is missing or access is denied.' >&2; exit 1; }
        cd "$dir"
        """ + "\n";

    async Task<string> ResolveAsync(DeploymentTarget target, CancellationToken token) => await new DeviceService(adb).ResolveAsync(target, token);
    async Task<string> ShellAsync(string transport, string script, CancellationToken token, int timeout = 30) =>
        (await adb.RunAsync(["-s", transport, "shell", script], token, timeout)).RequireSuccess();

    public async Task<IReadOnlyList<HeadsetFile>> ListAsync(DeploymentTarget target, string path, CancellationToken token)
    {
        path = NormalizePath(path);
        string transport = await ResolveAsync(target, token);
        string output = await ShellAsync(transport, DirectoryGuard(path) + """
            find . -mindepth 1 -maxdepth 1 -printf '%M\000%s\000%T@\000%f\000'
            """, token);
        if (output.Length == 0) return [];
        string[] fields = output.Split('\0');
        if (fields.Length % 4 != 1 || fields[^1].Length != 0) throw new IOException("Could not read the directory listing. Refresh and try again.");
        var entries = new List<HeadsetFile>((fields.Length - 1) / 4);
        for (int i = 0; i < fields.Length - 1; i += 4)
        {
            if (fields[i].Length == 0 || !long.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                || !decimal.TryParse(fields[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal modified)
                || modified < -62135596800m || modified >= 253402300800m)
                throw new IOException("The headset returned an invalid directory entry.");
            string kind = fields[i][0] switch { 'd' => "Folder", '-' => "File", 'l' => "Link", _ => "Other" };
            string name = fields[i + 3];
            // Unusual control-character names remain visible, but cannot be used as paths.
            entries.Add(new(name, path + "/" + name, kind, size, DateTimeOffset.FromUnixTimeSeconds((long)decimal.Floor(modified))));
        }
        return entries.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task CreateFolderAsync(DeploymentTarget target, string parent, string name, CancellationToken token)
    {
        ChildPath(parent, name);
        string transport = await ResolveAsync(target, token);
        await ShellAsync(transport, DirectoryGuard(parent) + $"mkdir -- {Quote(name)}", token);
    }

    public async Task MoveAsync(DeploymentTarget target, string source, string destination, CancellationToken token)
    {
        source = NormalizePath(source); destination = NormalizePath(destination);
        if (source == Root || destination == Root || destination.StartsWith(source + "/", StringComparison.Ordinal))
            throw new ArgumentException("Cannot move shared storage or move a folder inside itself.");
        string transport = await ResolveAsync(target, token);
        string script = DirectoryGuard(ParentPath(source)) + $"source=\"$dir\"/{Quote(NameOf(source))}\n" + DirectoryGuard(ParentPath(destination)) + $$"""
            destination="$dir"/{{Quote(NameOf(destination))}}
            [ -e "$source" ] || { echo 'Source no longer exists.' >&2; exit 1; }
            [ ! -L "$source" ] || { echo 'Links cannot be moved or renamed.' >&2; exit 1; }
            [ ! -e "$destination" ] && [ ! -L "$destination" ] || { echo 'An item with that name already exists.' >&2; exit 1; }
            mv -nT -- "$source" "$destination"
            [ ! -e "$source" ] || { echo 'The move did not complete.' >&2; exit 1; }
            """;
        await ShellAsync(transport, script, token);
    }

    public async Task DeleteAsync(DeploymentTarget target, string path, CancellationToken token)
    {
        path = NormalizePath(path);
        if (path == Root) throw new ArgumentException("Shared storage itself cannot be deleted.");
        string transport = await ResolveAsync(target, token);
        await ShellAsync(transport, DirectoryGuard(ParentPath(path)) + $"rm -r -- {Quote("./" + NameOf(path))}", token, 120);
    }

    public async Task UploadAsync(DeploymentTarget target, string parent, string localPath, bool replaceFile, CancellationToken token)
    {
        localPath = Path.GetFullPath(localPath);
        bool folder = Directory.Exists(localPath);
        if (!folder && !File.Exists(localPath)) throw new FileNotFoundException("The selected local item no longer exists.", localPath);
        string name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar));
        ChildPath(parent, name);
        string transport = await ResolveAsync(target, token);
        string stageName = ".questtool-upload-" + Guid.NewGuid().ToString("N");
        string stage = ChildPath(parent, stageName);
        string guard = DirectoryGuard(parent) + $"destination=\"$dir\"/{Quote(name)}\nstage=\"$dir\"/{Quote(stageName)}\n";
        string check = replaceFile && !folder
            ? "[ ! -L \"$destination\" ] && { [ ! -e \"$destination\" ] || [ -f \"$destination\" ]; }"
            : "[ ! -e \"$destination\" ] && [ ! -L \"$destination\" ]";
        await ShellAsync(transport, guard + check + " || { echo 'Destination already exists or cannot be replaced.' >&2; exit 1; }", token);
        try
        {
            (await adb.RunAsync(["-s", transport, "push", localPath, stage], token, 3600)).RequireSuccess();
            string verify = folder ? "" : $"[ \"$(stat -c %s -- \"$stage\")\" = {Quote(new FileInfo(localPath).Length.ToString(CultureInfo.InvariantCulture))} ] || {{ echo 'Upload size verification failed.' >&2; exit 1; }}\n";
            string move = replaceFile && !folder ? "mv -fT" : "mv -nT";
            await ShellAsync(transport, guard + check + " || { echo 'Destination changed during upload.' >&2; exit 1; }\n" + verify + $"{move} -- \"$stage\" \"$destination\"\n[ ! -e \"$stage\" ] || {{ echo 'Upload was not moved into place.' >&2; exit 1; }}", token);
        }
        finally
        {
            try { await ShellAsync(transport, guard + "rm -rf -- \"$stage\"", CancellationToken.None, 10); }
            catch { /* A disconnected headset may retain the uniquely named upload staging item. */ }
        }
    }

    public async Task DownloadAsync(DeploymentTarget target, HeadsetFile entry, string localDestination, CancellationToken token, IProgress<FileTransferProgress>? progress = null)
    {
        string path = NormalizePath(entry.Path);
        if (path == Root || entry.IsLink) throw new ArgumentException("Select a file or folder, rather than shared storage or a link.");
        localDestination = Path.GetFullPath(localDestination);
        if (File.Exists(localDestination) || Directory.Exists(localDestination)) throw new IOException("Choose a new local name; the destination already exists.");
        string transport = await ResolveAsync(target, token);
        await ShellAsync(transport, DirectoryGuard(entry.IsDirectory ? path : ParentPath(path)) + $"[ ! -L {Quote(path)} ] || {{ echo 'Links cannot be downloaded.' >&2; exit 1; }}", token);
        string parent = Path.GetDirectoryName(localDestination) ?? throw new ArgumentException("Choose a local destination folder.");
        string stage = Path.Combine(parent, ".questtool-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timer = Stopwatch.StartNew();
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var transfer = adb.RunAsync(["-s", transport, "pull", path, stage], transferCancellation.Token, 3600);
            bool stalled = false;
            if (!entry.IsDirectory)
            {
                long previousBytes = 0;
                TimeSpan lastProgress = timer.Elapsed;
                while (!transfer.IsCompleted)
                {
                    long bytes = 0;
                    try { if (File.Exists(stage)) bytes = new FileInfo(stage).Length; }
                    catch (IOException) { }
                    if (bytes != previousBytes) { previousBytes = bytes; lastProgress = timer.Elapsed; }
                    if (timer.Elapsed - lastProgress > TimeSpan.FromSeconds(30))
                    {
                        stalled = true;
                        transferCancellation.Cancel();
                    }
                    progress?.Report(new(bytes, entry.Size, timer.Elapsed));
                    await Task.WhenAny(transfer, Task.Delay(500));
                }
            }
            try { (await transfer).RequireSuccess(); }
            catch (OperationCanceledException) when (stalled && !token.IsCancellationRequested)
            { throw new IOException("Download stalled: no data arrived for 30 seconds. Try USB, or reconnect Wi-Fi in Headsets and retry."); }
            if (entry.IsDirectory) Directory.Move(stage, localDestination);
            else File.Move(stage, localDestination);
            if (!entry.IsDirectory) progress?.Report(new(new FileInfo(localDestination).Length, entry.Size, timer.Elapsed));
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            else if (File.Exists(stage)) File.Delete(stage);
        }
    }
}
