using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed record ApkInfo(string Path, string Package, long VersionCode, string VersionName)
{
    public string ObbDirectory => "/sdcard/Android/obb/" + Package;
    public static ApkInfo Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("This APK has no AndroidManifest.xml.");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("APK manifest is too large.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        try { return Parse(path, memory.ToArray()); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException)
        { throw new InvalidDataException("Invalid Android binary manifest.", ex); }
    }

    // Android resource chunks and typed values; no SDK/build-tools dependency.
    internal static ApkInfo Parse(string path, byte[] bytes)
    {
        var data = bytes.AsSpan();
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2));
        if (data.Length < 8 || U16(0) != 3 || U32(4) != data.Length) throw new InvalidDataException("Expected an Android binary XML manifest.");
        string[] strings = [];
        string S(uint index) => index == uint.MaxValue ? "" : strings[checked((int)index)];
        for (int offset = U16(2); offset < data.Length;)
        {
            ushort type = U16(offset), header = U16(offset + 2);
            int size = checked((int)U32(offset + 4));
            if (header < 8 || size < header || offset > data.Length - size) throw new InvalidDataException("Invalid manifest chunk.");
            if (type == 1)
            {
                int count = checked((int)U32(offset + 8));
                bool utf8 = (U32(offset + 16) & 0x100) != 0;
                int start = checked(offset + (int)U32(offset + 20));
                if (count < 0 || count > size / 4) throw new InvalidDataException("Invalid string pool.");
                strings = new string[count];
                for (int i = 0; i < count; i++)
                {
                    int p = checked(start + (int)U32(offset + header + i * 4));
                    int Length8() { int n = bytes[p++]; return (n & 0x80) == 0 ? n : ((n & 0x7f) << 8) | bytes[p++]; }
                    int Length16() { int n = U16(p); p += 2; if ((n & 0x8000) == 0) return n; int next = U16(p); p += 2; return checked(((n & 0x7fff) << 16) | next); }
                    if (utf8) { Length8(); int n = Length8(); strings[i] = Encoding.UTF8.GetString(bytes, p, n); }
                    else { int n = Length16(); strings[i] = Encoding.Unicode.GetString(bytes, p, checked(n * 2)); }
                }
            }
            else if (type == 0x102 && S(U32(offset + 20)) == "manifest")
            {
                int start = offset + 16 + U16(offset + 24), stride = U16(offset + 26), count = U16(offset + 28);
                if (stride < 20 || start < offset || (long)start + (long)stride * count > offset + size) throw new InvalidDataException("Invalid manifest attributes.");
                string package = "", versionName = "";
                uint version = 0, major = 0;
                bool hasVersion = false;
                for (int i = 0; i < count; i++)
                {
                    int p = start + i * stride;
                    string name = S(U32(p + 4));
                    byte valueType = bytes[p + 15];
                    uint value = U32(p + 16);
                    string text = U32(p + 8) != uint.MaxValue ? S(U32(p + 8)) : valueType == 3 ? S(value) : value.ToString();
                    if (name == "package" && U32(p) == uint.MaxValue) package = text;
                    if (name == "split" && text.Length > 0) throw new InvalidDataException("Split APKs are not supported. Export a standalone/universal APK.");
                    if (S(U32(p)) != "http://schemas.android.com/apk/res/android") continue;
                    if (name == "versionCode") { version = valueType is 0x10 or 0x11 ? value : uint.Parse(text); hasVersion = true; }
                    if (name == "versionCodeMajor") major = valueType is 0x10 or 0x11 ? value : uint.Parse(text);
                    if (name == "versionName") versionName = valueType == 3 || U32(p + 8) != uint.MaxValue ? text : "";
                }
                if (!Regex.IsMatch(package, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$") || !hasVersion || major > int.MaxValue)
                    throw new InvalidDataException("APK package name or version code is invalid/missing.");
                return new(path, package, ((long)major << 32) | version, versionName);
            }
            offset = checked(offset + size);
        }
        throw new InvalidDataException("No manifest element found in APK.");
    }

    public void ValidateObbs(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("OBB file not found.", path);
            string name = System.IO.Path.GetFileName(path);
            // Expansion versions may intentionally be retained across APK updates.
            var match = Regex.Match(name, @"^(main|patch)\.([0-9]+)\." + Regex.Escape(Package) + @"\.obb$");
            if (!match.Success)
                throw new InvalidDataException($"{name} must be named main.<version>.{Package}.obb or patch.<version>.{Package}.obb.");
            if (!uint.TryParse(match.Groups[2].Value, out uint version) || version > VersionCode)
                throw new InvalidDataException($"{name} has an invalid version, or a version newer than APK code {VersionCode}. Keep the original OBB name exported with your build.");
            if (new FileInfo(path).Length == 0) throw new InvalidDataException($"{name} is empty. Use the OBB exported with your APK.");
            if (!names.Add(name) || names.Count > 2 || names.Count(n => n.StartsWith(name.Split('.')[0] + ".")) > 1)
                throw new InvalidDataException("Use at most one main OBB and one patch OBB.");
        }
    }
}
