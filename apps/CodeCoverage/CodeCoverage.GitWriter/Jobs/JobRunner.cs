using System.Diagnostics;
using CodeCoverage.Git;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>Maps git's (already scrubbed) stderr onto the contract's reasons, as the mirror does.</summary>
internal static class GitErrors
{
    public static string Classify(string error)
    {
        error = error.ToLowerInvariant();
        if (error.Contains("authentication failed") || error.Contains("could not read username") || error.Contains("could not read password")
            || error.Contains(" 401") || error.Contains(" 403") || error.Contains("access denied") || error.Contains("permission denied")
            || error.Contains("permission to ") && error.Contains("denied"))
            return JobReasons.Auth;
        if (error.Contains("not found") || error.Contains(" 404") || error.Contains("does not appear to be a git repository") || error.Contains("couldn't find remote ref"))
            return JobReasons.NotFound;
        return JobReasons.Error;
    }
}

/// <summary>The end of a job that cannot go on; the runner turns it into a <c>failed</c> answer with this reason.</summary>
internal sealed class JobAbortedException(string reason, string? detail = null) : Exception(detail ?? reason)
{
    public string Reason { get; } = reason;
    public string? Detail { get; } = detail;
}

/// <summary>
/// Runs one job at a time per slot, each in its own directory, and enforces the job caps that are not the container's
/// (docs/coverage_merge_queue_PRD.md S8): the slot limit (<see cref="GitWriterOptions.MaxConcurrentJobs"/>), the host
/// free-space floor before anything starts, the wall clock (the running git process tree is killed when it passes), and the
/// disk cap, measured by a watchdog while the job runs. The job directory is deleted when the job ends, however it ends.
/// </summary>
public sealed class JobRunner : IDisposable
{
    private readonly GitRunner git;
    private readonly GitWriterOptions options;
    private readonly ILogger<JobRunner> logger;
    private readonly SemaphoreSlim slots;

    public JobRunner(GitRunner git, IOptions<GitWriterOptions> options, ILogger<JobRunner> logger)
    {
        this.git = git;
        this.options = options.Value;
        this.logger = logger;
        if (this.options.MaxConcurrentJobs is < 1 or > 2)
            throw new InvalidOperationException("GitWriter:MaxConcurrentJobs must be 1 or 2: the container's memory and pids limits are sized per job (S8).");
        slots = new SemaphoreSlim(this.options.MaxConcurrentJobs, this.options.MaxConcurrentJobs);
        Directory.CreateDirectory(this.options.Root);
    }

    /// <summary>How often the watchdog measures a job's directory.</summary>
    internal TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Free bytes on the jobs' volume.</summary>
    public long FreeBytes => new DriveInfo(Path.GetFullPath(options.Root)).AvailableFreeSpace;

    /// <summary>Jobs currently holding a slot.</summary>
    public int Running => options.MaxConcurrentJobs - slots.CurrentCount;

    /// <summary>Runs <paramref name="body"/> in a fresh job directory. Not tied to the request: a caller that gives up must
    /// not abort a push half way; the job's own deadline bounds it.</summary>
    public async Task<T> RunAsync<T>(string kind, string repo, Func<JobContext, Task<T>> body, Func<string, string?, string?, long, T> fail)
    {
        var clock = Stopwatch.StartNew();
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(options.SlotWaitSeconds)))
        {
            logger.LogInformation("{Kind} for {Repo}: busy", kind, repo);
            return fail(JobOutcomes.Busy, null, null, clock.ElapsedMilliseconds);
        }
        try
        {
            if (FreeBytes < options.MinFreeBytes)
                return Fail(JobReasons.DiskFloor, null);

            var directory = Path.Combine(Path.GetFullPath(options.Root), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.JobTimeoutSeconds));
            var context = new JobContext(git, directory, deadline.Token, clock, TimeSpan.FromSeconds(options.JobTimeoutSeconds), options.MaxJobBytes);
            using var watchdog = new Timer(_ => Watch(context, deadline), null, WatchInterval, WatchInterval);
            try
            {
                var result = await body(context);
                logger.LogInformation("{Kind} for {Repo} finished after {Elapsed} ms", kind, repo, clock.ElapsedMilliseconds);
                return result;
            }
            catch (Exception) when (context.OverDisk)
            {
                return Fail(JobReasons.TooLarge, $"The job directory passed {options.MaxJobBytes} bytes.");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return Fail(JobReasons.Timeout, $"The job ran out of its {options.JobTimeoutSeconds} s.");
            }
            catch (JobAbortedException aborted)
            {
                return Fail(aborted.Reason, aborted.Detail);
            }
            catch (GitCommandException failure)
            {
                // Its message carries git's stderr, which GitRunner already scrubbed of the credential.
                return Fail(GitErrors.Classify(failure.Result?.StandardError ?? failure.Message), failure.Message);
            }
            finally
            {
                await watchdog.DisposeAsync();
                DeleteTree(directory);
            }
        }
        finally
        {
            slots.Release();
        }

        T Fail(string reason, string? detail)
        {
            logger.LogInformation("{Kind} for {Repo}: failed ({Reason}) after {Elapsed} ms", kind, repo, reason, clock.ElapsedMilliseconds);
            return fail(JobOutcomes.Failed, reason, detail, clock.ElapsedMilliseconds);
        }
    }

    private void Watch(JobContext context, CancellationTokenSource deadline)
    {
        try
        {
            if (!deadline.IsCancellationRequested && SizeOf(context.Directory) > options.MaxJobBytes)
            {
                context.OverDisk = true;
                deadline.Cancel();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A file vanished while being measured; the next tick measures again.
        }
    }

    /// <summary>A directory's size: the sum of its files' lengths.</summary>
    internal static long SizeOf(string directory)
        => Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }).Sum(f => f.Length)
            : 0;

    /// <summary>Deletes a job directory, clearing the read-only attribute git puts on pack files (Windows refuses to delete
    /// those otherwise). Retried briefly: a just-killed process may still hold a handle.</summary>
    internal static void DeleteTree(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                        file.Attributes &= ~FileAttributes.ReadOnly;
                }
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    public void Dispose() => slots.Dispose();
}

/// <summary>One job's directory, deadline and git. Every command runs with what is left of the job's time, the job's
/// credential and the writer's memory bounds.</summary>
public sealed class JobContext
{
    /// <summary>Per-command git config bounding git's own memory and threads (S8's 512 MB and 64 pids per job are the
    /// container's limits; these keep a single git within them).</summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> WriterConfig =
    [
        new("pack.threads", "1"),
        new("core.packedGitLimit", "256m"),
        new("core.deltaBaseCacheLimit", "64m"),
    ];

    private readonly GitRunner git;
    private readonly Stopwatch clock;
    private readonly TimeSpan budget;
    private readonly long maxBytes;

    internal JobContext(GitRunner git, string directory, CancellationToken deadline, Stopwatch clock, TimeSpan budget, long maxBytes)
    {
        this.git = git;
        this.clock = clock;
        this.budget = budget;
        this.maxBytes = maxBytes;
        Directory = directory;
        Deadline = deadline;
        GitDirectory = Path.Combine(directory, "repo.git");
        Scratch = Path.Combine(directory, "scratch");
        System.IO.Directory.CreateDirectory(Scratch);
    }

    public string Directory { get; }

    /// <summary>The job's bare repository.</summary>
    public string GitDirectory { get; }

    /// <summary>Throwaway index files and the patch.</summary>
    public string Scratch { get; }

    public CancellationToken Deadline { get; }

    public TimeSpan Remaining => budget - clock.Elapsed;

    public long ElapsedMilliseconds => clock.ElapsedMilliseconds;

    internal volatile bool OverDisk;

    /// <summary>The disk cap checked at once, after each step that writes objects (clone, fetch, merge); the watchdog
    /// covers the time in between.</summary>
    public void EnsureWithinDisk()
    {
        if (JobRunner.SizeOf(Directory) > maxBytes)
        {
            OverDisk = true;
            throw new JobAbortedException(JobReasons.TooLarge);
        }
    }

    /// <summary>Runs git in the job's repository (or with no repository for clone); throws when the job's time is up.</summary>
    public async Task<GitResult> GitAsync(IReadOnlyList<string> arguments, GitCredential? credential, bool inRepository = true, string? input = null,
        string? index = null, GitIdentity? author = null, GitIdentity? committer = null)
    {
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException(Deadline);
        var result = await git.RunAsync(new GitInvocation
        {
            Arguments = arguments,
            GitDirectory = inRepository ? GitDirectory : null,
            Credential = credential,
            Timeout = remaining,
            StandardInput = input,
            IndexFile = index,
            Author = author,
            Committer = committer,
            Config = WriterConfig,
        }, Deadline);
        if (result.TimedOut)
            throw new OperationCanceledException(Deadline);
        return result;
    }

    /// <summary>As <see cref="GitAsync"/>, throwing <see cref="GitCommandException"/> on a non-zero exit.</summary>
    public async Task<GitResult> RequireAsync(IReadOnlyList<string> arguments, GitCredential? credential = null, string? input = null,
        string? index = null, GitIdentity? author = null, GitIdentity? committer = null, bool inRepository = true)
    {
        var result = await GitAsync(arguments, credential, inRepository, input: input, index: index, author: author, committer: committer);
        if (!result.Succeeded)
            throw new GitCommandException(arguments[0], result);
        return result;
    }
}
