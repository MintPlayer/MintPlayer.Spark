using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitMirror.Git;

/// <summary>A forge credential for one git process. It travels only in that process's environment (D26) and is never
/// written to disk, an argument or a log.</summary>
public sealed record GitCredential(string Username, string Password)
{
    /// <summary>The value of the <c>http.extraHeader</c> git sends: <c>Authorization: Basic base64(user:pass)</c>.</summary>
    internal string Header => "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    // Never let a record's generated ToString print the token into a log or an assertion message.
    public override string ToString() => $"GitCredential {{ Username = {Username}, Password = *** }}";
}

/// <summary>One git command. <see cref="Arguments"/> starts at the subcommand; the hardening flags are added by
/// <see cref="GitRunner"/>.</summary>
public sealed class GitInvocation
{
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The mirror the command runs in (<c>git -C</c>), or null for clone and ls-remote.</summary>
    public string? GitDirectory { get; init; }

    public GitCredential? Credential { get; init; }

    /// <summary>A read of the mirror: runs with <c>GIT_NO_LAZY_FETCH=1</c> (D25), so a missing object fails instead of
    /// downloading.</summary>
    public bool Read { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The subcommand, the only part of a command that is logged.</summary>
    internal string Verb => Arguments.Count > 0 ? Arguments[0] : "";
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Stopped = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Stopped;
}

/// <summary>
/// The only class that starts <c>git</c> (PRD §4.1, D26). Every process:
/// <list type="bullet">
/// <item>gets its arguments as an argument list, never a shell string;</item>
/// <item>runs with an environment cleared of every inherited <c>GIT_*</c> variable and then hardened:
/// <c>GIT_TERMINAL_PROMPT=0</c>, empty <c>GIT_ASKPASS</c>, <c>GIT_CONFIG_NOSYSTEM=1</c>, <c>GIT_CONFIG_GLOBAL</c> pointing
/// at an empty file, and <c>GIT_NO_LAZY_FETCH=1</c> on reads;</item>
/// <item>gets the D26 per-command <c>-c</c> flags (protocol v2, file/ext transports off, an empty read-only hooks
/// directory, no auto maintenance or gc, no submodule recursion, no credential helper);</item>
/// <item>receives a credential only through <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_0</c>/<c>GIT_CONFIG_VALUE_0</c>
/// as <c>http.extraHeader</c>, which git never persists (unlike <c>clone --config</c>);</item>
/// <item>is killed with its whole tree when its timeout passes;</item>
/// <item>has its stderr scrubbed of the credential before anything is logged or returned.</item>
/// </list>
/// </summary>
public sealed class GitRunner
{
    private const int MaxCapturedError = 64 * 1024;

    private readonly ILogger<GitRunner> logger;
    private readonly bool allowInsecureHttp;
    private readonly string globalConfigPath;

    public GitRunner(IOptions<GitMirrorOptions> options, ILogger<GitRunner> logger)
    {
        this.logger = logger;
        allowInsecureHttp = options.Value.AllowInsecureHttp;
        HooksPath = EnsureEmptyHooksDirectory(options.Value.HooksPath);
        globalConfigPath = EmptyGlobalConfig();
    }

    /// <summary>The empty, read-only directory every process gets as <c>core.hooksPath</c> (D5).</summary>
    public string HooksPath { get; }

    /// <summary>Test seam: called with each process's start info just before it starts.</summary>
    internal event Action<ProcessStartInfo>? ProcessStarting;

    /// <summary>The D26 configuration every command runs with, also written into each mirror's config.</summary>
    internal IEnumerable<KeyValuePair<string, string>> HardenedConfig()
    {
        yield return new("protocol.version", "2");
        yield return new("protocol.allow", "never");
        yield return new("protocol.https.allow", "always");
        if (allowInsecureHttp)
            yield return new("protocol.http.allow", "always");
        yield return new("protocol.file.allow", "never");
        yield return new("protocol.ext.allow", "never");
        yield return new("core.hooksPath", HooksPath);
        yield return new("maintenance.auto", "false");
        yield return new("gc.auto", "0");
        yield return new("submodule.recurse", "false");
        yield return new("log.mailmap", "false");
    }

    internal ProcessStartInfo CreateStartInfo(GitInvocation invocation)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("GIT_", comparer) || key.Equals("SSH_ASKPASS", comparer))
                psi.Environment.Remove(key);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = globalConfigPath;
        psi.Environment["GIT_PROTOCOL_FROM_USER"] = "0";
        if (invocation.Read)
            psi.Environment["GIT_NO_LAZY_FETCH"] = "1";
        if (invocation.Credential is { } credential)
        {
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = credential.Header;
        }

        if (invocation.GitDirectory is { } dir)
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
        }
        foreach (var (key, value) in HardenedConfig())
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"{key}={value}");
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        foreach (var argument in invocation.Arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    /// <summary>Runs a command to completion with its stdout buffered.</summary>
    public async Task<GitResult> RunAsync(GitInvocation invocation, CancellationToken cancellationToken)
    {
        using var process = Start(invocation);
        var stderr = DrainErrorAsync(process);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var timedOut = await WaitAsync(process, invocation.Timeout, cancellationToken);
        var output = await stdout;
        return Finish(invocation, process, timedOut, output, await stderr);
    }

    /// <summary>Runs a command and hands each stdout line to <paramref name="onLine"/>; returning false stops reading and
    /// kills the process (the ls-remote ref quota stops at its limit this way, D26).</summary>
    public async Task<GitResult> RunLinesAsync(GitInvocation invocation, Func<string, bool> onLine, CancellationToken cancellationToken)
    {
        using var process = Start(invocation);
        var stderr = DrainErrorAsync(process);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(invocation.Timeout);
        var stopped = false;
        var timedOut = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (!onLine(line))
                {
                    stopped = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }
        if (stopped)
            Kill(process);
        if (!timedOut)
            timedOut = await WaitAsync(process, invocation.Timeout, cancellationToken);
        var result = Finish(invocation, process, timedOut, "", await stderr);
        return stopped ? result with { Stopped = true } : result;
    }

    /// <summary>Starts a long-running read (<c>git log</c>) whose stdout is streamed by the caller. Disposing the handle
    /// kills the process if it still runs.</summary>
    public GitReadStream OpenRead(GitInvocation invocation)
    {
        var process = Start(invocation);
        return new GitReadStream(process, DrainErrorAsync(process), this, invocation);
    }

    internal string Scrub(string text, GitCredential? credential)
    {
        if (credential is null || text.Length == 0)
            return text;
        var basic = credential.Header["Authorization: Basic ".Length..];
        foreach (var secret in new[] { credential.Header, basic, credential.Password })
        {
            if (secret.Length > 0)
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        return text;
    }

    private Process Start(GitInvocation invocation)
    {
        var psi = CreateStartInfo(invocation);
        ProcessStarting?.Invoke(psi);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start.");
        // git never reads stdin here; closing it means nothing can ever block on a prompt.
        process.StandardInput.Close();
        return process;
    }

    private static async Task<string> DrainErrorAsync(Process process)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await process.StandardError.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            if (builder.Length < MaxCapturedError)
                builder.Append(buffer, 0, Math.Min(read, MaxCapturedError - builder.Length));
        }
        return builder.ToString();
    }

    /// <summary>Waits for exit; on timeout kills the tree and returns true. A caller cancellation kills and throws.</summary>
    private static async Task<bool> WaitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
    }

    internal GitResult Finish(GitInvocation invocation, Process process, bool timedOut, string output, string error)
    {
        var exitCode = process.HasExited ? process.ExitCode : -1;
        var scrubbed = Scrub(error, invocation.Credential);
        if (timedOut)
            logger.LogWarning("git {Verb} timed out after {Timeout} and was killed", invocation.Verb, invocation.Timeout);
        else if (exitCode != 0)
            logger.LogInformation("git {Verb} exited with {ExitCode}: {Error}", invocation.Verb, exitCode, scrubbed.Trim());
        return new GitResult(exitCode, Scrub(output, invocation.Credential), scrubbed, timedOut);
    }

    private static string EnsureEmptyHooksDirectory(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "codecoverage-gitmirror-hooks-empty")
            : configured;
        Directory.CreateDirectory(path);
        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException($"The hooks directory {path} must be empty (D5).");
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(configured))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>An empty global config: <c>/dev/null</c> on Linux (D26); on Windows, where git for Windows' handling of
    /// <c>/dev/null</c> is not something to rely on, an empty read-only file.</summary>
    private static string EmptyGlobalConfig()
    {
        if (!OperatingSystem.IsWindows())
            return "/dev/null";
        var path = Path.Combine(Path.GetTempPath(), "codecoverage-gitmirror-empty.gitconfig");
        if (!File.Exists(path))
        {
            try
            {
                File.WriteAllBytes(path, []);
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Created concurrently by another runner.
            }
        }
        if (new FileInfo(path).Length != 0)
            throw new InvalidOperationException($"{path} must be empty.");
        return path;
    }
}

/// <summary>A running read whose stdout the caller streams (<see cref="GitRunner.OpenRead"/>).</summary>
public sealed class GitReadStream : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> error;
    private readonly GitRunner runner;
    private readonly GitInvocation invocation;

    internal GitReadStream(Process process, Task<string> error, GitRunner runner, GitInvocation invocation)
    {
        this.process = process;
        this.error = error;
        this.runner = runner;
        this.invocation = invocation;
    }

    public Stream Output => process.StandardOutput.BaseStream;

    /// <summary>After the output reached its end: the exit status, with stderr scrubbed.</summary>
    public async Task<GitResult> CompleteAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(invocation.Timeout);
        await process.WaitForExitAsync(deadline.Token);
        return runner.Finish(invocation, process, false, "", await error);
    }

    public async ValueTask DisposeAsync()
    {
        GitRunner.Kill(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await error;
        }
        finally
        {
            process.Dispose();
        }
    }
}
