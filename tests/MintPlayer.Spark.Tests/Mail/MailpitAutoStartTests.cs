using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Transports;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 — <c>Spark:Mail:Mailpit:AutoStart</c> through a fake launcher: nothing real is started, no
/// Mailpit and no Docker are needed. The one Windows-only test exercises the real Job Object.
/// </summary>
public class MailpitAutoStartTests
{
    private readonly FakeLauncher launcher = new();
    private readonly RecordingLogger logger = new();

    private MailpitAutoStart Create(string environment = "Development", Action<SparkMailMailpitOptions>? configure = null)
    {
        var options = new SparkMailOptions { Mailpit = { AutoStart = true } };
        configure?.Invoke(options.Mailpit);
        return new MailpitAutoStart(Options.Create(options),
            new HostingEnvironment { EnvironmentName = environment, ApplicationName = "SparkTests", ContentRootPath = Path.GetTempPath() },
            launcher, logger);
    }

    private static async Task RunAsync(MailpitAutoStart service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.Launch;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_Development_nothing_is_started_and_one_line_says_so(string environment)
    {
        var service = Create(environment);
        await RunAsync(service);
        await service.StopAsync(CancellationToken.None);

        launcher.Calls.Should().BeEmpty();
        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("ignored outside Development")).Should().Be(1);
    }

    [Fact]
    public async Task Without_AutoStart_nothing_happens()
    {
        var service = Create(configure: o => o.AutoStart = false);
        await RunAsync(service);
        launcher.Calls.Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SparkMailMailpitMode.Binary)]
    [InlineData(SparkMailMailpitMode.Docker)]
    public async Task A_listener_on_the_SMTP_port_is_reused_and_nothing_is_started(SparkMailMailpitMode mode)
    {
        launcher.Listening = true;
        var service = Create(configure: o => { o.Mode = mode; o.Port = 2025; });
        await RunAsync(service);
        await service.StopAsync(CancellationToken.None);

        launcher.Calls.Should().Equal(["listening 2025"]);
    }

    [Fact]
    public async Task A_missing_binary_is_one_warning_with_install_hints_and_no_throw()
    {
        launcher.Executable = null;
        var service = Create();
        await RunAsync(service);
        await service.StopAsync(CancellationToken.None);

        launcher.Calls.Should().NotContain(c => c.StartsWith("start", StringComparison.Ordinal));
        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which;
        warning.Should().Contain("winget install axllent.mailpit").And.Contain("scoop install mailpit").And.Contain("github.com/axllent/mailpit/releases");
    }

    [Fact]
    public async Task The_binary_gets_the_configured_ports_and_is_stopped_at_shutdown()
    {
        var service = Create(configure: o => { o.Port = 2025; o.UiPort = 9025; });
        await RunAsync(service);

        launcher.Calls.Should().Contain("start mailpit --smtp 127.0.0.1:2025 --listen 127.0.0.1:9025");
        logger.Entries.Should().Contain(e => e.Message.Contains("http://localhost:9025"));
        launcher.Process!.Stopped.Should().BeFalse();

        await service.StopAsync(CancellationToken.None);
        launcher.Process.Stopped.Should().BeTrue();
        launcher.Process.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Docker_runs_a_labelled_pinned_container_and_stops_it_at_shutdown()
    {
        var service = Create(configure: o => { o.Mode = SparkMailMailpitMode.Docker; o.Port = 2025; o.UiPort = 9025; });
        await RunAsync(service);

        launcher.Calls.Should().Contain(
            $"docker run --rm -d --name spark-mailpit --label spark.mailpit.owner=SparkTests -p 2025:1025 -p 9025:8025 axllent/mailpit:{SparkMailMailpitOptions.DefaultImageTag}");
        await service.StopAsync(CancellationToken.None);
        launcher.Calls.Last().Should().Be("docker stop spark-mailpit");
    }

    [Fact]
    public async Task Docker_reuses_a_running_container_with_the_name_and_never_stops_it()
    {
        launcher.Inspect = new MailpitCommandResult(0, "true|SparkTests", "");
        var service = Create(configure: o => o.Mode = SparkMailMailpitMode.Docker);
        await RunAsync(service);
        await service.StopAsync(CancellationToken.None);

        launcher.Calls.Should().NotContain(c => c.StartsWith("docker run", StringComparison.Ordinal));
        launcher.Calls.Should().NotContain(c => c.StartsWith("docker start", StringComparison.Ordinal));
        launcher.Calls.Should().NotContain(c => c.StartsWith("docker stop", StringComparison.Ordinal), "it was running before us");
    }

    [Theory]
    [InlineData("false|SparkTests", true)]
    [InlineData("false|", false)]
    [InlineData("false|SomeOtherApp", false)]
    public async Task Docker_starts_a_stopped_container_with_the_name_and_stops_it_only_when_it_carries_our_label(string inspect, bool stopped)
    {
        launcher.Inspect = new MailpitCommandResult(0, inspect, "");
        var service = Create(configure: o => o.Mode = SparkMailMailpitMode.Docker);
        await RunAsync(service);
        await service.StopAsync(CancellationToken.None);

        launcher.Calls.Should().Contain("docker start spark-mailpit");
        launcher.Calls.Should().NotContain(c => c.StartsWith("docker run", StringComparison.Ordinal));
        launcher.Calls.Contains("docker stop spark-mailpit").Should().Be(stopped);
    }

    [Fact]
    public async Task Docker_missing_or_its_daemon_down_is_one_warning_and_nothing_else()
    {
        launcher.DockerInstalled = false;
        var missing = Create(configure: o => o.Mode = SparkMailMailpitMode.Docker);
        await RunAsync(missing);
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Contain("not installed");

        logger.Entries.Clear();
        launcher.DockerInstalled = true;
        launcher.Info = new MailpitCommandResult(1, "", "Cannot connect to the Docker daemon");
        var down = Create(configure: o => o.Mode = SparkMailMailpitMode.Docker);
        await RunAsync(down);
        await down.StopAsync(CancellationToken.None);
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Contain("daemon is not running");
        launcher.Calls.Should().NotContain(c => c.StartsWith("docker run", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void Closing_the_job_handle_kills_the_process_in_it()
    {
        var info = new ProcessStartInfo("ping") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-n", "60", "127.0.0.1" })
            info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        child.BeginOutputReadLine();
        try
        {
            var job = WindowsJobObject.TryAssign(child);
            job.Should().NotBeNull("assignment succeeds on Windows 8+, nested jobs included");

            job!.Dispose();

            // A failure bound only: the child should be gone long before this.
            child.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue("closing the last job handle kills its processes");
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Job Objects are Windows-only.";
        }
    }

    private sealed class FakeLauncher : IMailpitLauncher
    {
        public List<string> Calls { get; } = [];
        public bool Listening { get; set; }
        public string? Executable { get; set; } = "mailpit";
        public bool DockerInstalled { get; set; } = true;
        public MailpitCommandResult Info { get; set; } = new(0, "27.0.0", "");
        public MailpitCommandResult Inspect { get; set; } = new(1, "", "Error: No such object: spark-mailpit");
        public FakeProcess? Process { get; private set; }

        public bool IsListening(int port)
        {
            Calls.Add($"listening {port}");
            return Listening;
        }

        public string? FindExecutable(string? configured) => Executable;

        public IMailpitProcess Start(string executable, IReadOnlyList<string> arguments)
        {
            Calls.Add($"start {executable} {string.Join(' ', arguments)}");
            return Process = new FakeProcess();
        }

        public Task<MailpitCommandResult?> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add($"docker {string.Join(' ', arguments)}");
            if (!DockerInstalled)
                return Task.FromResult<MailpitCommandResult?>(null);
            return Task.FromResult<MailpitCommandResult?>(arguments[0] switch
            {
                "info" => Info,
                "inspect" => Inspect,
                _ => new MailpitCommandResult(0, "", ""),
            });
        }
    }

    private sealed class FakeProcess : IMailpitProcess
    {
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public bool KillsWithHost => true;
        public void Stop() => Stopped = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger : ILogger<MailpitAutoStart>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
