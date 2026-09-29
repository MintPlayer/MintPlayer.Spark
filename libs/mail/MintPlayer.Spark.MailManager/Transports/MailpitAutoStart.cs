using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace MintPlayer.Spark.MailManager.Transports;

/// <summary>
/// <c>Spark:Mail:Mailpit:AutoStart</c>: starts Mailpit with the host, in Development only. Never blocks
/// startup (the launch runs in the background; the mail lanes' retry covers the first seconds), adopts
/// whatever already listens on the SMTP port, and on shutdown stops only what this process started.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binary mode on Windows</b>: the process is put in a Job Object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, held for the host's lifetime, so Mailpit dies with the
/// host even when it crashes or a debugger stops it. On Linux and macOS there is no reliable
/// parent-death hook from <see cref="Process.Start()"/> (and no polling watchdog by design): the
/// graceful stop at shutdown applies, and a leftover from a crash is adopted by the next run, because
/// it is already listening on the port.
/// </para>
/// <para>
/// <b>Docker mode</b>: the container is labelled <c>spark.mailpit.owner={ApplicationName}</c>, and only a
/// labelled container this process created or started is stopped.
/// </para>
/// </remarks>
internal sealed class MailpitAutoStart(
    IOptions<SparkMailOptions> options,
    IHostEnvironment environment,
    IMailpitLauncher launcher,
    ILogger<MailpitAutoStart> logger) : IHostedService
{
    internal const string OwnerLabel = "spark.mailpit.owner";

    private IMailpitProcess? process;
    private string? startedContainer;

    /// <summary>The background launch; tests await it.</summary>
    internal Task Launch { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value.Mailpit;
        if (!o.AutoStart)
            return Task.CompletedTask;

        if (!environment.IsDevelopment())
        {
            logger.LogInformation("Spark:Mail:Mailpit:AutoStart is ignored outside Development (environment {Environment}); nothing is started.", environment.EnvironmentName);
            return Task.CompletedTask;
        }

        Launch = Task.Run(() => LaunchAsync(o), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LaunchAsync(SparkMailMailpitOptions o)
    {
        try
        {
            if (launcher.IsListening(o.Port))
            {
                logger.LogInformation("Something already listens on port {Port}; using it as Mailpit, nothing started. Mailpit UI (if it is Mailpit): {Url}", o.Port, UiUrl(o));
                return;
            }

            if (o.Mode == SparkMailMailpitMode.Docker)
                await StartDockerAsync(o);
            else
                StartBinary(o);
        }
        catch (Exception ex)
        {
            // Never fatal: mail stays queued and is retried.
            logger.LogWarning(ex, "Mailpit could not be started; mail is retried until an SMTP server listens on port {Port}.", o.Port);
        }
    }

    private void StartBinary(SparkMailMailpitOptions o)
    {
        var executable = launcher.FindExecutable(o.ExecutablePath);
        if (executable is null)
        {
            logger.LogWarning(
                "Mailpit is not installed ({Where}); nothing started, and mail is retried until an SMTP server listens on port {Port}. " +
                "Install it with 'winget install axllent.mailpit', 'scoop install mailpit', or from https://github.com/axllent/mailpit/releases, " +
                "or set Spark:Mail:Mailpit:Mode=Docker.",
                o.ExecutablePath is { Length: > 0 } path ? $"not found at {path}" : "not on PATH", o.Port);
            return;
        }

        process = launcher.Start(executable, ["--smtp", $"127.0.0.1:{o.Port}", "--listen", $"127.0.0.1:{o.UiPort}"]);
        if (OperatingSystem.IsWindows() && !process.KillsWithHost)
            logger.LogWarning("Mailpit could not be tied to the host's lifetime (Job Object assignment failed); it is stopped at shutdown, but a crash may leave it running (the next run adopts it).");
        logger.LogInformation("Mailpit started ({Executable}): SMTP on port {Port}, UI at {Url}", executable, o.Port, UiUrl(o));
    }

    private async Task StartDockerAsync(SparkMailMailpitOptions o)
    {
        var info = await launcher.DockerAsync(["info", "--format", "{{.ServerVersion}}"], CancellationToken.None);
        if (info is null)
        {
            logger.LogWarning("Docker is not installed; Mailpit not started. Install Docker or set Spark:Mail:Mailpit:Mode=Binary.");
            return;
        }
        if (info.ExitCode != 0)
        {
            logger.LogWarning("The Docker daemon is not running ({Error}); Mailpit not started.", info.Error);
            return;
        }

        var owner = environment.ApplicationName;
        var name = o.ContainerName;
        var inspect = await launcher.DockerAsync(
            ["inspect", "--format", "{{.State.Running}}|{{index .Config.Labels \"" + OwnerLabel + "\"}}", name], CancellationToken.None);
        if (inspect is { ExitCode: 0 })
        {
            var parts = inspect.Output.Split('|', 2);
            var running = string.Equals(parts[0].Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var labelled = parts.Length == 2 && string.Equals(parts[1].Trim(), owner, StringComparison.Ordinal);
            if (running)
            {
                logger.LogInformation("Reusing the running container {Container} as Mailpit; UI at {Url}", name, UiUrl(o));
                return;
            }

            var start = await launcher.DockerAsync(["start", name], CancellationToken.None);
            if (start is not { ExitCode: 0 })
            {
                logger.LogWarning("The existing container {Container} could not be started ({Error}); Mailpit not started.", name, start?.Error);
                return;
            }
            // Only ours to stop when it carries our label: a container someone else made stays up.
            startedContainer = labelled ? name : null;
            logger.LogInformation("Started the existing container {Container} as Mailpit; UI at {Url}", name, UiUrl(o));
            return;
        }

        var run = await launcher.DockerAsync(
            ["run", "--rm", "-d", "--name", name, "--label", $"{OwnerLabel}={owner}",
             "-p", $"{o.Port}:1025", "-p", $"{o.UiPort}:8025", $"axllent/mailpit:{o.ImageTag}"],
            CancellationToken.None);
        if (run is not { ExitCode: 0 })
        {
            logger.LogWarning("docker run for Mailpit failed ({Error}); Mailpit not started.", run?.Error);
            return;
        }
        startedContainer = name;
        logger.LogInformation("Mailpit container {Container} (axllent/mailpit:{Tag}) started; UI at {Url}", name, o.ImageTag, UiUrl(o));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Launch.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        if (process is { } started)
        {
            process = null;
            started.Stop();
            started.Dispose();
        }

        if (startedContainer is { } container)
        {
            startedContainer = null;
            try
            {
                await launcher.DockerAsync(["stop", container], cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(ex, "The Mailpit container {Container} could not be stopped.", container);
            }
        }
    }

    private static string UiUrl(SparkMailMailpitOptions o) => $"http://localhost:{o.UiPort}";
}

/// <summary>The outcome of one <c>docker</c> command.</summary>
internal sealed record MailpitCommandResult(int ExitCode, string Output, string Error);

/// <summary>A started Mailpit process.</summary>
internal interface IMailpitProcess : IDisposable
{
    /// <summary>Whether it is in a kill-on-close Job Object (Windows), so it dies with the host.</summary>
    bool KillsWithHost { get; }

    /// <summary>Kills it and its whole process tree.</summary>
    void Stop();
}

/// <summary>The seam between <see cref="MailpitAutoStart"/> and the operating system (faked in tests).</summary>
internal interface IMailpitLauncher
{
    bool IsListening(int port);

    /// <summary><paramref name="configured"/> when it exists, else <c>mailpit</c> from <c>PATH</c>; null when there is none.</summary>
    string? FindExecutable(string? configured);

    IMailpitProcess Start(string executable, IReadOnlyList<string> arguments);

    /// <summary>Runs <c>docker</c>; null when docker is not installed.</summary>
    Task<MailpitCommandResult?> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class MailpitLauncher(ILogger<MailpitLauncher> logger) : IMailpitLauncher
{
    public bool IsListening(int port)
        => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    public string? FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var name = OperatingSystem.IsWindows() ? "mailpit.exe" : "mailpit";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory.Trim('"'), name); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public IMailpitProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Drained below: an undrained redirected pipe fills and blocks the child.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        var started = Process.Start(info) ?? throw new InvalidOperationException($"{executable} did not start.");
        started.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogDebug("mailpit: {Line}", e.Data); };
        started.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogDebug("mailpit: {Line}", e.Data); };
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();

        var job = OperatingSystem.IsWindows() ? WindowsJobObject.TryAssign(started) : null;
        return new MailpitProcess(started, job);
    }

    public async Task<MailpitCommandResult?> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        Process? docker;
        try
        {
            docker = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return null;
        }
        if (docker is null)
            return null;

        using (docker)
        {
            var output = docker.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = docker.StandardError.ReadToEndAsync(cancellationToken);
            await docker.WaitForExitAsync(cancellationToken);
            return new MailpitCommandResult(docker.ExitCode, (await output).Trim(), (await error).Trim());
        }
    }

    private sealed class MailpitProcess(Process process, SafeHandle? job) : IMailpitProcess
    {
        public bool KillsWithHost => job is { IsInvalid: false };

        public void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // A failure bound, not a delay: Kill returns before the process is gone.
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            job?.Dispose();
            process.Dispose();
        }
    }
}

/// <summary>A Windows Job Object that kills its processes when its last handle closes.</summary>
internal static class WindowsJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>A new kill-on-close job with <paramref name="process"/> in it, or null (logged by the caller) when that failed.</summary>
    internal static SafeHandle? TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !AssignProcessToJobObject(job, process.SafeHandle))
        {
            job.Dispose();
            return null;
        }
        return job;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
