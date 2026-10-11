using System.Diagnostics;
using System.Globalization;

namespace MintPlayer.Spark.Dashboards.Host;

/// <summary>A filesystem's size: <see cref="TotalBytes"/> and <see cref="FreeBytes"/> (available to this process).</summary>
public sealed record SparkDiskUsage(long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>Used share in percent (0–100), one decimal.</summary>
    public double UsedPercent => TotalBytes > 0 ? Math.Round(UsedBytes * 100.0 / TotalBytes, 1) : 0;
}

/// <summary>
/// Memory in use and the ceiling it is measured against: the container's limit, else the host's total memory.
/// </summary>
public sealed record SparkMemoryUsage(long UsedBytes, long? LimitBytes);

/// <summary>
/// Reads host and container figures for a dashboard provider (<c>docs/spark_dashboards_PRD.md</c> §9.2). Each read
/// returns null when the figure is not available, and never throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disk:</b> <c>DriveInfo("/")</c>. Inside a container on overlay2 this is the host's root filesystem: S10 measured
/// the same total, used and available bytes inside <c>coverage-app</c> as <c>df -B1 /</c> on the host.
/// </para>
/// <para>
/// <b>Memory:</b> the cgroup v2 <c>memory.current</c> (the container, page cache included) against
/// <c>memory.max</c>; S10 found <c>max</c> (no limit) in production, so the ceiling falls back to <c>MemTotal</c> of
/// <c>/proc/meminfo</c>. Without cgroup v2 the host's <c>MemTotal − MemAvailable</c>. On Windows (development) the
/// process's working set against the memory the GC sees.
/// </para>
/// </remarks>
public static class SparkHostMetrics
{
    /// <summary>The filesystem holding <paramref name="path"/> (default <c>/</c>; on Windows the current drive).</summary>
    public static SparkDiskUsage? ReadDisk(string path = "/")
    {
        try
        {
            var root = OperatingSystem.IsWindows() && path == "/" ? Path.GetPathRoot(Environment.CurrentDirectory) ?? path : path;
            var drive = new DriveInfo(root);
            return drive.IsReady ? new SparkDiskUsage(drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The container's (or else the host's) memory in use and its ceiling.</summary>
    public static SparkMemoryUsage? ReadMemory() => ReadMemory("/sys/fs/cgroup", "/proc");

    internal static SparkMemoryUsage? ReadMemory(string cgroupRoot, string procRoot)
    {
        try
        {
            var meminfo = ReadMeminfo(Path.Combine(procRoot, "meminfo"));
            meminfo.TryGetValue("MemTotal", out var hostTotal);

            if (ReadLong(Path.Combine(cgroupRoot, "memory.current")) is { } current)
            {
                var limit = ReadLong(Path.Combine(cgroupRoot, "memory.max"));
                return new SparkMemoryUsage(current, limit ?? (hostTotal > 0 ? hostTotal : null));
            }

            if (hostTotal > 0 && meminfo.TryGetValue("MemAvailable", out var available))
                return new SparkMemoryUsage(hostTotal - available, hostTotal);

            if (!OperatingSystem.IsLinux())
            {
                using var process = Process.GetCurrentProcess();
                var gcTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                return new SparkMemoryUsage(process.WorkingSet64, gcTotal > 0 ? gcTotal : null);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A file holding one integer (<c>memory.current</c>), or null when absent or <c>max</c>.</summary>
    internal static long? ReadLong(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path).Trim();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary><c>/proc/meminfo</c> in bytes, by field name; empty when absent.</summary>
    internal static Dictionary<string, long> ReadMeminfo(string path)
    {
        var fields = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return fields;
        foreach (var line in File.ReadLines(path))
        {
            // "MemTotal:       16314424 kB"
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                continue;
            fields[line[..colon]] = parts.Length > 1 && parts[1] == "kB" ? amount * 1024 : amount;
        }
        return fields;
    }
}

/// <summary>
/// CPU use between two readings, in percent of the cores available (0–100): the cgroup v2 <c>cpu.stat</c>
/// <c>usage_usec</c> (the container) against <c>cpu.max</c>'s quota, or every core when it is <c>max</c> (S10); without
/// cgroups, the process's own processor time. Register it as a singleton: it keeps the previous reading.
/// </summary>
public sealed class SparkCpuUsage
{
    private readonly string cgroupRoot;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private (long UsageMicroseconds, long AtTimestamp)? previous;

    public SparkCpuUsage() : this("/sys/fs/cgroup", TimeProvider.System)
    {
    }

    internal SparkCpuUsage(string cgroupRoot, TimeProvider time)
    {
        this.cgroupRoot = cgroupRoot;
        this.time = time;
    }

    /// <summary>The use since the previous call, one decimal; null on the first call or when nothing can be read.</summary>
    public double? Read()
    {
        long usage;
        try
        {
            usage = ReadUsageMicroseconds() ?? ProcessMicroseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        var now = time.GetTimestamp();
        lock (gate)
        {
            var last = previous;
            previous = (usage, now);
            if (last is not { } p)
                return null;
            var elapsedMicroseconds = time.GetElapsedTime(p.AtTimestamp, now).Ticks / 10.0;
            if (elapsedMicroseconds <= 0)
                return null;
            var share = (usage - p.UsageMicroseconds) / (elapsedMicroseconds * Cores());
            return Math.Round(Math.Clamp(share * 100, 0, 100), 1);
        }
    }

    private static long ProcessMicroseconds()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.Ticks / 10;
    }

    private long? ReadUsageMicroseconds()
    {
        var path = Path.Combine(cgroupRoot, "cpu.stat");
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("usage_usec ", StringComparison.Ordinal)
                && long.TryParse(line["usage_usec ".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var usage))
                return usage;
        }
        return null;
    }

    /// <summary>The cores the container may use: <c>cpu.max</c>'s quota / period, else every core.</summary>
    internal double Cores()
    {
        try
        {
            var path = Path.Combine(cgroupRoot, "cpu.max");
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period)
                    && quota > 0 && period > 0)
                    return quota / (double)period;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return Environment.ProcessorCount;
    }
}
