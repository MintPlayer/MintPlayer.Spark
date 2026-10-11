using MintPlayer.Spark.Dashboards.Host;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// The host metrics' sources (<c>docs/spark_dashboards_PRD.md</c> §9.2, S10) over fake cgroup v2 and <c>/proc</c>
/// files: the container's memory against its limit or the host total, and CPU as a usage delta.
/// </summary>
public sealed class SparkHostMetricsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-host-metrics-" + Guid.NewGuid().ToString("N"));
    private string Cgroup => Path.Combine(root, "cgroup");
    private string Proc => Path.Combine(root, "proc");

    public SparkHostMetricsTests()
    {
        Directory.CreateDirectory(Cgroup);
        Directory.CreateDirectory(Proc);
        File.WriteAllText(Path.Combine(Proc, "meminfo"), "MemTotal:       4000 kB\nMemFree:         500 kB\nMemAvailable:   1000 kB\n");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Memory_is_the_containers_current_against_the_host_total_when_the_limit_is_max()
    {
        File.WriteAllText(Path.Combine(Cgroup, "memory.current"), "1048576\n");
        File.WriteAllText(Path.Combine(Cgroup, "memory.max"), "max\n");

        var memory = SparkHostMetrics.ReadMemory(Cgroup, Proc);

        memory.Should().Be(new SparkMemoryUsage(1048576, 4000 * 1024), "S10: memory.max is 'max' in production");
    }

    [Fact]
    public void Memory_is_measured_against_the_containers_limit_when_it_has_one()
    {
        File.WriteAllText(Path.Combine(Cgroup, "memory.current"), "1048576");
        File.WriteAllText(Path.Combine(Cgroup, "memory.max"), "2097152");

        SparkHostMetrics.ReadMemory(Cgroup, Proc).Should().Be(new SparkMemoryUsage(1048576, 2097152));
    }

    [Fact]
    public void Without_cgroup_v2_memory_falls_back_to_proc_meminfo()
    {
        SparkHostMetrics.ReadMemory(Cgroup, Proc).Should().Be(new SparkMemoryUsage((4000 - 1000) * 1024, 4000 * 1024));
    }

    [Fact]
    public void Cpu_is_the_usage_delta_over_the_elapsed_time_and_the_available_cores()
    {
        var time = new SteppedTime();
        File.WriteAllText(Path.Combine(Cgroup, "cpu.max"), "200000 100000\n"); // two cores
        File.WriteAllText(Path.Combine(Cgroup, "cpu.stat"), "usage_usec 1000000\nuser_usec 800000\n");
        var cpu = new SparkCpuUsage(Cgroup, time);

        cpu.Read().HasValue.Should().BeFalse("the first reading has nothing to compare with");
        time.Advance(TimeSpan.FromSeconds(1));
        File.WriteAllText(Path.Combine(Cgroup, "cpu.stat"), "usage_usec 1500000\nuser_usec 900000\n");

        cpu.Read().Should().Be(25, "0.5 s of CPU in 1 s over 2 cores");
    }

    [Fact]
    public void Cpu_uses_every_core_when_the_quota_is_max()
    {
        File.WriteAllText(Path.Combine(Cgroup, "cpu.max"), "max 100000\n");

        new SparkCpuUsage(Cgroup, TimeProvider.System).Cores().Should().Be(Environment.ProcessorCount);
    }

    [Fact]
    public void The_disk_of_the_current_root_is_read_on_any_platform()
    {
        var disk = SparkHostMetrics.ReadDisk();

        disk.Should().NotBeNull();
        disk!.TotalBytes.Should().BeGreaterThan(0);
        disk.UsedPercent.Should().BeInRange(0, 100);
    }

    private sealed class SteppedTime : TimeProvider
    {
        private long ticks = 1;
        public void Advance(TimeSpan by) => ticks += by.Ticks;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
