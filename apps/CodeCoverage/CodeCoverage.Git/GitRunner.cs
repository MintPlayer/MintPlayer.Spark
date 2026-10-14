using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CodeCoverage.Git;

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

    /// <summary>Written to stdin as UTF-8 (no BOM), which is then closed: <c>update-index -z --index-info</c> and
    /// <c>commit-tree -F -</c> read their input this way. Null closes stdin at once.</summary>
    public string? StandardInput { get; init; }

    /// <summary>A throwaway index (<c>GIT_INDEX_FILE</c>) for building a tree in a bare repository without touching
    /// anything else (docs/coverage_merge_queue_PRD.md §5.3 step 5).</summary>
    public string? IndexFile { get; init; }

    /// <summary><c>GIT_AUTHOR_*</c> for <c>commit-tree</c>.</summary>
    public GitIdentity? Author { get; init; }

    /// <summary><c>GIT_COMMITTER_*</c> for <c>commit-tree</c>.</summary>
    public GitIdentity? Committer { get; init; }

    /// <summary>Extra <c>-c key=value</c> pairs for this command only, placed after the hardened ones (a later
    /// <c>-c</c> wins, so these may not name a hardened key; <see cref="GitRunner"/> refuses that).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Config { get; init; } = [];

    /// <summary>The subcommand, the only part of a command that is logged.</summary>
    internal string Verb => Arguments.Count > 0 ? Arguments[0] : "";
}

/// <summary>A commit identity passed to git through <c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c>. Name and email come
/// from forge data (a PR author), so anything git would mis-parse in an ident line is refused.</summary>
public sealed record GitIdentity
{
    public GitIdentity(string name, string email, DateTimeOffset? when = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['<', '>', '\n', '\r', '\0']) >= 0)
            throw new ArgumentException("A git identity name may not be empty or contain '<', '>' or a line break.", nameof(name));
        if (string.IsNullOrWhiteSpace(email) || email.IndexOfAny(['<', '>', '\n', '\r', '\0', ' ']) >= 0)
            throw new ArgumentException("A git identity email may not be empty or contain '<', '>', a space or a line break.", nameof(email));
        Name = name;
        Email = email;
        When = when;
    }

    public string Name { get; }
    public string Email { get; }

    /// <summary>The ident date; null lets git use the current time.</summary>
    public DateTimeOffset? When { get; }

    /// <summary>git's raw date format: <c>&lt;unix seconds&gt; +HHMM</c>.</summary>
    internal string? GitDate => When is { } when
        ? $"{when.ToUnixTimeSeconds()} {(when.Offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(when.Offset.Hours):00}{Math.Abs(when.Offset.Minutes):00}"
        : null;
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Stopped = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Stopped;
}

/// <summary>
/// The only class that starts <c>git</c> (docs/coverage_commit_graph_PRD.md §4.1, D26; shared with the merge-queue
/// writer, docs/coverage_merge_queue_PRD.md D2/M2). Every process:
/// <list type="bullet">
/// <item>gets its arguments as an argument list, never a shell string;</item>
/// <item>runs with an environment cleared of every inherited <c>GIT_*</c> variable and then hardened:
/// <c>GIT_TERMINAL_PROMPT=0</c>, empty <c>GIT_ASKPASS</c>, <c>GIT_CONFIG_NOSYSTEM=1</c>, <c>GIT_CONFIG_GLOBAL</c> pointing
/// at an empty file, and <c>GIT_NO_LAZY_FETCH=1</c> on reads;</item>
/// <item>gets the D26 per-command <c>-c</c> flags (protocol v2, file/ext transports off, an empty read-only hooks
/// directory, no auto maintenance or gc, no submodule recursion, no credential helper), plus the merge queue's
/// (§4.3, T8): <c>attr.tree</c> set to the empty tree so no <c>.gitattributes</c> from repository content can pick a
/// merge driver, <c>core.fsmonitor=false</c>, and <c>transfer.fsckObjects</c> when
/// <see cref="GitRunnerOptions.CheckReceivedObjects"/> is on;</item>
/// <item>receives a credential only through <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_0</c>/<c>GIT_CONFIG_VALUE_0</c>
/// as <c>http.extraHeader</c>, which git never persists (unlike <c>clone --config</c>);</item>
/// <item>is killed with its whole tree when its timeout passes;</item>
/// <item>has its stderr scrubbed of the credential before anything is logged or returned.</item>
/// </list>
/// </summary>
public sealed class GitRunner
{
    private const int MaxCapturedError = 64 * 1024;

    /// <summary>The SHA-1 empty tree. git knows it without the object being stored, so it works in every SHA-1
    /// repository; SHA-256 repositories are refused by the merge queue (forges serve SHA-1 only).</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private readonly ILogger<GitRunner> logger;
    private readonly bool allowInsecureHttp;
    private readonly bool checkReceivedObjects;
    private readonly string globalConfigPath;

    public GitRunner(GitRunnerOptions options, ILogger<GitRunner> logger)
    {
        this.logger = logger;
        allowInsecureHttp = options.AllowInsecureHttp;
        checkReceivedObjects = options.CheckReceivedObjects;
        HooksPath = EnsureEmptyHooksDirectory(options.HooksPath);
        globalConfigPath = EmptyGlobalConfig();
    }

    /// <summary>The empty, read-only directory every process gets as <c>core.hooksPath</c> (D5).</summary>
    public string HooksPath { get; }

    /// <summary>Test seam: called with each process's start info just before it starts.</summary>
    internal event Action<ProcessStartInfo>? ProcessStarting;

    /// <summary>The D26 configuration every command runs with, also written into each mirror's config.</summary>
    public IEnumerable<KeyValuePair<string, string>> HardenedConfig()
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
        // Merge queue (§4.3, T8). Attributes are read from the empty tree only, so an in-repo `.gitattributes` with
        // `merge=union`, `-merge` or a named driver cannot change how a merge resolves (S1: red with attr.tree set to the
        // PR's tree, green with this). GIT_ATTR_SOURCE is cleared with every other inherited GIT_* variable.
        yield return new("attr.tree", EmptyTree);
        yield return new("core.fsmonitor", "false");
        if (checkReceivedObjects)
            yield return new("transfer.fsckObjects", "true");
    }

    private static readonly Regex ConfigKey = new(@"^[A-Za-z][A-Za-z0-9-]*(\.[A-Za-z0-9-]+)*\.[A-Za-z][A-Za-z0-9-]*$", RegexOptions.CultureInvariant);

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
        if (invocation.IndexFile is { } index)
            psi.Environment["GIT_INDEX_FILE"] = Path.GetFullPath(index);
        SetIdentity(psi, "AUTHOR", invocation.Author);
        SetIdentity(psi, "COMMITTER", invocation.Committer);

        if (invocation.GitDirectory is { } dir)
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
        }
        var hardened = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "credential.helper" };
        foreach (var (key, value) in HardenedConfig())
        {
            hardened.Add(key);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"{key}={value}");
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("credential.helper=");
        foreach (var (key, value) in invocation.Config)
        {
            if (!ConfigKey.IsMatch(key) || hardened.Contains(key) || key.StartsWith("protocol.", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{key}' is not an allowed per-command git config key.", nameof(invocation));
            if (value.IndexOfAny(['\n', '\r', '\0']) >= 0)
                throw new ArgumentException($"The value for '{key}' contains a line break.", nameof(invocation));
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"{key}={value}");
        }
        foreach (var argument in invocation.Arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    private static void SetIdentity(ProcessStartInfo psi, string role, GitIdentity? identity)
    {
        if (identity is null)
            return;
        psi.Environment[$"GIT_{role}_NAME"] = identity.Name;
        psi.Environment[$"GIT_{role}_EMAIL"] = identity.Email;
        if (identity.GitDate is { } date)
            psi.Environment[$"GIT_{role}_DATE"] = date;
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
        // Stdin carries only the invocation's own input; closing it means nothing can ever block on a prompt. The input
        // is small (an index-info list, a commit message), well below the pipe buffer, so writing before stdout is
        // drained cannot deadlock.
        if (invocation.StandardInput is { } input)
        {
            try
            {
                process.StandardInput.BaseStream.Write(new UTF8Encoding(false).GetBytes(input));
            }
            catch (IOException)
            {
                // git exited before reading all of it; its exit code and stderr say why.
            }
        }
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
            ? Path.Combine(Path.GetTempPath(), "codecoverage-git-hooks-empty")
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
        var path = Path.Combine(Path.GetTempPath(), "codecoverage-git-empty.gitconfig");
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
