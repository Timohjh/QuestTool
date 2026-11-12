using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestTool.Core;

public sealed record DeviceMetric(string Name, string Value);

public static class DeviceDetails
{
    internal const string Command = """
        echo __SYSTEM__
        printf 'manufacturer='; getprop ro.soc.manufacturer
        printf 'soc='; getprop ro.soc.model
        printf 'android='; getprop ro.build.version.release
        printf 'build='; getprop ro.build.version.incremental
        printf 'kernel='; uname -r
        echo __MEMORY__
        head -n 6 /proc/meminfo
        echo __CPU_A__
        head -n 1 /proc/stat
        sleep 0.25
        echo __CPU_B__
        head -n 1 /proc/stat
        echo __CPU_CLOCK__
        for p in /sys/devices/system/cpu/cpufreq/policy*/scaling_cur_freq; do printf '%s ' "$p"; cat "$p" 2>/dev/null; done
        echo __GPU__
        for p in gpu_model gpuclk gpubusy; do printf '%s=' "$p"; cat "/sys/class/kgsl/kgsl-3d0/$p" 2>/dev/null; echo; done
        echo __THERMAL__
        dumpsys thermalservice 2>/dev/null
        echo __WIFI__
        cmd wifi status 2>/dev/null
        echo __BATTERY__
        dumpsys battery
        """;

    public static IReadOnlyList<DeviceMetric> Parse(string output)
    {
        var sections = Regex.Split(output.Replace("\r", ""), @"(?m)^__(\w+)__\s*\n");
        var values = new Dictionary<string, string>();
        for (int i = 1; i + 1 < sections.Length; i += 2) values[sections[i]] = sections[i + 1].Trim();
        string Section(string name) => values.GetValueOrDefault(name, "");
        string Match(string text, string pattern) => Regex.Match(text, pattern).Groups[1].Value;
        static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        var result = new List<DeviceMetric>();
        void Add(string name, string? value) => result.Add(new(name, string.IsNullOrWhiteSpace(value) ? "Unavailable" : value));
        string SystemValue(string key) => Match(Section("SYSTEM"), @"(?m)^" + key + @"=([^\n]*)");
        Add("Chipset", (SystemValue("manufacturer") + " " + SystemValue("soc")).Trim());
        Add("Android", SystemValue("android"));
        Add("OS build", SystemValue("build"));
        Add("Kernel", SystemValue("kernel"));
        var cpuA = Cpu(Section("CPU_A")); var cpuB = Cpu(Section("CPU_B"));
        long total = cpuB.Total - cpuA.Total, idle = cpuB.Idle - cpuA.Idle;
        Add("CPU usage · 250 ms sample", cpuA.Total > 0 && total > 0 && idle >= 0 && idle <= total ? $"{100d * (total - idle) / total:0.0}%" : "");
        var clocks = Regex.Matches(Section("CPU_CLOCK"), @"policy(\d+)/scaling_cur_freq\s+(\d+)");
        Add("CPU cluster clocks", string.Join(" · ", clocks.Select(m => Number(m.Groups[2].Value, out double khz) ? $"{khz / 1000:0} MHz (policy {m.Groups[1].Value})" : "")));
        string gpu = Section("GPU");
        Add("GPU", Match(gpu, @"gpu_model=([^\n]+)"));
        Add("GPU clock", Number(Match(gpu, @"gpuclk=(\d+)"), out double hz) ? $"{hz / 1000000:0} MHz" : "");
        var busy = Regex.Match(gpu, @"gpubusy=\s*(\d+)\s+(\d+)");
        Add("GPU busy · driver sample", Number(busy.Groups[1].Value, out double active) && Number(busy.Groups[2].Value, out double time) && time > 0 && active <= time ? $"{active * 100 / time:0.0}%" : "");
        string mem = Section("MEMORY");
        bool memoryValid = Number(Match(mem, @"(?m)^MemTotal:\s*(\d+)"), out double memory) && memory > 0;
        Add("RAM total", memoryValid ? $"{memory / 1048576:0.00} GB" : "");
        Add("RAM used / available", memoryValid && Number(Match(mem, @"(?m)^MemAvailable:\s*(\d+)"), out double available) && available >= 0 && available <= memory
            ? $"{(memory - available) / 1048576:0.00} / {available / 1048576:0.00} GB" : "");
        string thermal = Section("THERMAL");
        string severity = Match(thermal, @"Thermal Status:\s*(\d+)");
        Add("Thermal status", severity switch { "0" => "Normal", "1" => "Light", "2" => "Moderate", "3" => "Severe", "4" => "Critical", "5" => "Emergency", "6" => "Shutdown", _ => "" });
        foreach ((string name, string type) in new[] { ("CPU temperature · highest sensor", "0"), ("GPU temperature · highest sensor", "1") })
        {
            double[] temperatures = Regex.Matches(thermal, @"Temperature\{mValue=([\d.]+), mType=" + type + @",").Select(m => Number(m.Groups[1].Value, out double degrees) ? degrees : double.NaN).Where(double.IsFinite).ToArray();
            Add(name, temperatures.Length > 0 ? $"{temperatures.Max():0.0} °C" : "");
        }
        Add("Battery temperature", Number(Match(Section("BATTERY"), @"(?m)^\s*temperature:\s*(\d+)"), out double battery) ? $"{battery / 10:0.0} °C" : "");
        var wifi = WifiService.ParseStatus(Section("WIFI"));
        Add("Wi-Fi network", wifi.Ssid); Add("Wi-Fi IP", wifi.IpAddress); Add("Wi-Fi signal", wifi.Signal); Add("Wi-Fi link", wifi.Link);
        return result;
    }

    static (long Total, long Idle) Cpu(string text)
    {
        string[] fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") return default;
        long[] ticks = fields.Skip(1).Take(8).Select(f => long.TryParse(f, out long n) && n >= 0 ? n : -1).ToArray();
        if (ticks.Any(n => n < 0)) return default;
        return (ticks.Sum(), ticks[3] + (ticks.Length > 4 ? ticks[4] : 0));
    }
}
