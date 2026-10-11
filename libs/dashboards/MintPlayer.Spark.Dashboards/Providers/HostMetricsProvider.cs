using MintPlayer.Spark.Dashboards.Host;

namespace MintPlayer.Spark.Dashboards.Providers;

/// <summary>
/// The built-in host metrics (<c>docs/spark_dashboards_PRD.md</c> §9.2), from <see cref="SparkHostMetrics"/>:
/// <list type="bullet">
/// <item><c>HostDisk</c>: a meter of used of total bytes of <c>/</c> (the host's disk, S10); <c>HostDiskFree</c> in
/// bytes; <c>HostDiskUsedPercent</c>.</item>
/// <item><c>Memory</c>: a meter of the container's memory in use of its limit (or the host total);
/// <c>MemoryUsed</c> in bytes.</item>
/// <item><c>Cpu</c>: percent of the available cores since the previous reading (empty on the first).</item>
/// </list>
/// Host RAM and CPU are deliberately not shown: a container sees only the host totals without its own share.
/// </summary>
public sealed class HostMetricsProvider(SparkCpuUsage cpu) : IDashboardProvider
{
    public Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        if (SparkHostMetrics.ReadDisk() is { } disk)
        {
            values.Set("HostDisk", DashboardValues.Meter(disk.UsedBytes, disk.TotalBytes));
            values.Set("HostDiskFree", disk.FreeBytes);
            values.Set("HostDiskUsedPercent", disk.UsedPercent);
        }
        if (SparkHostMetrics.ReadMemory() is { } memory)
        {
            values.Set("Memory", DashboardValues.Meter(memory.UsedBytes, memory.LimitBytes));
            values.Set("MemoryUsed", memory.UsedBytes);
        }
        values.Set("Cpu", cpu.Read());
        return Task.CompletedTask;
    }
}
