using System.Diagnostics;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Graph;

namespace CodeCoverage.GitMirror.Mirror;

/// <summary>
/// Clones and fetches mirrors (PRD §4.1, D26 recipe), one operation per repository at a time.
/// <list type="bullet">
/// <item><b>Join (D19):</b> a fetch for a repository that already has one in flight awaits that same operation and gets
/// its result; it never starts a second clone. A messaging redelivery after a claim expired mid-clone therefore costs
/// nothing.</item>
/// <item><b>Clone:</b> the floor (D20), then the ref quota by <c>ls-remote</c> (stops reading at the limit), then a bare,
/// treeless clone without <c>--no-tags</c> into a partial directory, configured, refs packed, commit-graph written,
/// measured against the size quota, and only then moved into place. Anything refused or failed deletes the partial
/// directory, so no directory is left behind.</item>
/// <item><b>Fetch:</b> <c>fetch --prune</c> on the heads and tags refspecs, <c>pack-refs --all</c>,
/// <c>commit-graph write --reachable --split</c>, then the quotas again.</item>
/// <item><b>Delete:</b> aborts an in-flight operation, waits for it, closes the repository's snapshot readers and removes
/// the directory.</item>
/// </list>
/// </summary>
public sealed class MirrorService(GitRunner git, MirrorPaths paths, DiskGuard disk, SnapshotCache snapshots, ILogger<MirrorService> logger)
{
    private sealed class Operation
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<FetchResponse> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<RepoKey, Operation> inFlight = [];
    private readonly Dictionary<RepoKey, SemaphoreSlim> gates = [];

    /// <summary>Clones the repository if it has no mirror, else fetches it; joins an operation already in flight. With
    /// <paramref name="fetchOnly"/> a missing mirror is answered <c>refused</c>/<c>missing</c> without starting git (D40);
    /// joining a clone already in flight is still allowed, its result is the caller's.</summary>
    public async Task<FetchResponse> FetchAsync(RepoKey key, Uri cloneUrl, GitCredential? credential, FetchQuota quota, bool fetchOnly = false)
    {
        var response = await StartOrJoin(key, cloneUrl, credential, quota, fetchOnly);
        // A clone-or-fetch that joined a fetch-only operation whose mirror vanished meanwhile must still clone.
        if (!fetchOnly && response is { Outcome: FetchOutcomes.Refused, Reason: FetchReasons.Missing })
            response = await StartOrJoin(key, cloneUrl, credential, quota, fetchOnly: false);
        return response;
    }

    private Task<FetchResponse> StartOrJoin(RepoKey key, Uri cloneUrl, GitCredential? credential, FetchQuota quota, bool fetchOnly)
    {
        Operation operation;
        lock (inFlight)
        {
            if (inFlight.TryGetValue(key, out var running))
                return running.Result.Task;
            if (fetchOnly && !paths.Exists(key))
                return Task.FromResult(Missing(key));
            operation = new Operation();
            inFlight[key] = operation;
        }

        _ = Task.Run(async () =>
        {
            FetchResponse response;
            try
            {
                response = await RunAsync(key, cloneUrl, credential, quota, fetchOnly, operation.Cancellation.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mirror operation for {Repo} failed", key);
                response = new FetchResponse(FetchOutcomes.Failed, FetchReasons.Error, 0, null, 0, SafeFreeBytes(), 0);
            }
            lock (inFlight)
            {
                if (inFlight.TryGetValue(key, out var current) && current == operation)
                    inFlight.Remove(key);
            }
            operation.Result.SetResult(response);
        });
        return operation.Result.Task;
    }

    /// <summary>Removes the mirror and its snapshots; idempotent. An operation in flight is aborted and awaited first.</summary>
    public async Task DeleteAsync(RepoKey key)
    {
        Operation? running;
        lock (inFlight)
            inFlight.TryGetValue(key, out running);
        if (running is not null)
        {
            running.Cancellation.Cancel();
            await running.Result.Task;
        }

        var gate = Gate(key);
        await gate.WaitAsync();
        try
        {
            await snapshots.InvalidateAsync(key, keepRefsHash: null);
            MirrorPaths.DeleteTree(paths.Mirror(key));
            foreach (var partial in paths.Partials(key).ToList())
                MirrorPaths.DeleteTree(partial);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(RepoKey key)
    {
        lock (gates)
        {
            if (!gates.TryGetValue(key, out var gate))
                gates[key] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    private FetchResponse Missing(RepoKey key)
    {
        logger.LogInformation("Mirror {Repo}: fetch-only and no mirror, nothing started", key);
        return new FetchResponse(FetchOutcomes.Refused, FetchReasons.Missing, 0, null, 0, SafeFreeBytes(), 0);
    }

    private async Task<FetchResponse> RunAsync(RepoKey key, Uri cloneUrl, GitCredential? credential, FetchQuota quota, bool fetchOnly, CancellationToken aborted)
    {
        var clock = Stopwatch.StartNew();
        var gate = Gate(key);
        try
        {
            await gate.WaitAsync(aborted);
        }
        catch (OperationCanceledException)
        {
            return Respond(new RunContext(key, "", null, quota, clock, CancellationToken.None), FetchOutcomes.Failed, FetchReasons.Error);
        }
        try
        {
            // Checked again under the gate: a DELETE may have removed the mirror since StartOrJoin looked.
            if (fetchOnly && !paths.Exists(key))
                return Missing(key);
            var context = new RunContext(key, cloneUrl.AbsoluteUri, credential, quota, clock, aborted);
            if (disk.BelowFloor())
                return Respond(context, FetchOutcomes.Refused, FetchReasons.DiskFloor);
            return paths.Exists(key) ? await FetchExistingAsync(context) : await CloneAsync(context);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // Aborted by DELETE, which removes whatever is left.
            return Respond(new RunContext(key, "", null, quota, clock, CancellationToken.None), FetchOutcomes.Failed, FetchReasons.Error);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record RunContext(RepoKey Key, string Url, GitCredential? Credential, FetchQuota Quota, Stopwatch Clock, CancellationToken Aborted)
    {
        public TimeSpan Remaining => Quota.Timeout - Clock.Elapsed;
    }

    private async Task<FetchResponse> CloneAsync(RunContext context)
    {
        var partial = paths.NewPartial(context.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        try
        {
            // The ref quota, before anything is written (D26): count heads and tags, stop reading at the limit.
            var refs = 0;
            var listing = await RunTimedAsync(context, ["ls-remote", "--heads", "--tags", context.Url], null, line =>
            {
                if (!line.EndsWith("^{}", StringComparison.Ordinal))
                    refs++;
                return refs <= context.Quota.MaxRefs;
            });
            if (listing is null)
                return Respond(context, FetchOutcomes.Failed, FetchReasons.Timeout);
            if (listing.Stopped)
                return Respond(context, FetchOutcomes.Refused, FetchReasons.TooManyRefs);
            if (!listing.Succeeded)
                return Respond(context, FetchOutcomes.Failed, Classify(listing));

            // Clone without --no-tags so the clone itself writes packed-refs (D26). An empty --template= copies no hooks (D5).
            // The scalar hardening keys are persisted by the clone itself (--config writes them into the new repository's
            // config), one process instead of one per key. Never the credential: that stays in the environment.
            var cloneArgs = new List<string> { "clone", "--bare", "--quiet", "--filter=tree:0", "--template=" };
            foreach (var (configKey, value) in git.HardenedConfig().Append(new("fetch.writeCommitGraph", "false")))
                cloneArgs.Add($"--config={configKey}={value}");
            cloneArgs.AddRange([context.Url, partial]);
            var clone = await RunTimedAsync(context, cloneArgs, null);
            if (clone is null)
                return Respond(context, FetchOutcomes.Failed, FetchReasons.Timeout);
            if (!clone.Succeeded)
                return Respond(context, FetchOutcomes.Failed, Classify(clone));

            if (!await ConfigureAsync(context, partial))
                return Respond(context, FetchOutcomes.Failed, FetchReasons.Error);
            if (await MaintainAsync(context, partial) is { } failure)
                return Respond(context, FetchOutcomes.Failed, failure);

            var size = MirrorPaths.SizeOf(partial);
            if (size > context.Quota.MaxBytes)
                return Respond(context, FetchOutcomes.Refused, FetchReasons.TooLarge);

            var mirror = paths.Mirror(context.Key);
            MirrorPaths.DeleteTree(mirror);
            Directory.Move(partial, mirror);
            return await DoneAsync(context, FetchOutcomes.Cloned, mirror);
        }
        finally
        {
            // Every refused, failed or timed-out clone ends here with its partial directory still present.
            MirrorPaths.DeleteTree(partial);
        }
    }

    private async Task<FetchResponse> FetchExistingAsync(RunContext context)
    {
        var mirror = paths.Mirror(context.Key);
        // The forge's URL may change (a rename); the mirror stays keyed by the stable id.
        var url = await RunTimedAsync(context, ["config", "remote.origin.url", context.Url], mirror);
        if (url is not { Succeeded: true })
            return Respond(context, FetchOutcomes.Failed, url is null ? FetchReasons.Timeout : FetchReasons.Error);

        var fetch = await RunTimedAsync(context, ["fetch", "--prune", "--quiet", "origin"], mirror);
        if (fetch is null)
            return Respond(context, FetchOutcomes.Failed, FetchReasons.Timeout);
        if (!fetch.Succeeded)
            return Respond(context, FetchOutcomes.Failed, Classify(fetch));
        if (await MaintainAsync(context, mirror) is { } failure)
            return Respond(context, FetchOutcomes.Failed, failure);

        // The quotas hold for the mirror's whole life: one that outgrew them is removed and recorded, never left half-served.
        if (MirrorPaths.SizeOf(mirror) > context.Quota.MaxBytes)
            return await RemoveAndRefuseAsync(context, mirror, FetchReasons.TooLarge);
        var state = await MirrorRefsState.ReadAsync(git, mirror, context.Aborted);
        if (state is not null && state.Refs.Count > context.Quota.MaxRefs)
            return await RemoveAndRefuseAsync(context, mirror, FetchReasons.TooManyRefs);
        return await DoneAsync(context, FetchOutcomes.Fetched, mirror);
    }

    private async Task<FetchResponse> RemoveAndRefuseAsync(RunContext context, string mirror, string reason)
    {
        await snapshots.InvalidateAsync(context.Key, keepRefsHash: null);
        MirrorPaths.DeleteTree(mirror);
        return Respond(context, FetchOutcomes.Refused, reason);
    }

    /// <summary>The rest of the D26 repository config (the hardening keys came with the clone): heads and tags refspecs only
    /// (fork isolation, §5.5), and <c>tagOpt --no-tags</c> so the explicit tag refspec (which <c>--prune</c> honours) is
    /// the only way tags arrive.</summary>
    private async Task<bool> ConfigureAsync(RunContext context, string mirror)
    {
        string[][] commands =
        [
            ["config", "--unset-all", "remote.origin.fetch"],
            ["config", "--add", "remote.origin.fetch", "+refs/heads/*:refs/heads/*"],
            ["config", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*"],
            ["config", "remote.origin.tagOpt", "--no-tags"],
        ];
        foreach (var command in commands)
        {
            var result = await RunTimedAsync(context, command, mirror);
            // --unset-all exits 5 when a bare clone had no fetch refspec to unset.
            if (result is null || (!result.Succeeded && !(command[1] == "--unset-all" && result.ExitCode == 5)))
                return false;
        }
        return true;
    }

    /// <summary>Packs refs and writes the split commit-graph explicitly (D26): no auto maintenance ever runs.</summary>
    private async Task<string?> MaintainAsync(RunContext context, string mirror)
    {
        foreach (var command in new[] { new[] { "pack-refs", "--all" }, ["commit-graph", "write", "--reachable", "--split"] })
        {
            var result = await RunTimedAsync(context, command, mirror);
            if (result is null)
                return FetchReasons.Timeout;
            if (!result.Succeeded)
                return FetchReasons.Error;
        }
        return null;
    }

    private async Task<FetchResponse> DoneAsync(RunContext context, string outcome, string mirror)
    {
        var state = await MirrorRefsState.ReadAsync(git, mirror, context.Aborted);
        long commits = 0;
        if (state is { Refs.Count: > 0 })
        {
            var count = await git.RunAsync(new GitInvocation { GitDirectory = mirror, Read = true, Arguments = ["rev-list", "--count", "--branches", "--tags"] }, context.Aborted);
            if (count.Succeeded)
                long.TryParse(count.StandardOutput.Trim(), out commits);
        }
        if (state is not null)
            await snapshots.InvalidateAsync(context.Key, keepRefsHash: state.RefsHash);
        return new FetchResponse(outcome, null, MirrorPaths.SizeOf(mirror), state?.RefsHash, commits, SafeFreeBytes(), context.Clock.ElapsedMilliseconds);
    }

    /// <summary>Runs one step of the operation with what is left of the quota's time; null when that ran out (the
    /// process tree has then been killed).</summary>
    private async Task<GitResult?> RunTimedAsync(RunContext context, IReadOnlyList<string> arguments, string? gitDirectory, Func<string, bool>? onLine = null)
    {
        var remaining = context.Remaining;
        if (remaining <= TimeSpan.Zero)
            return null;
        var invocation = new GitInvocation
        {
            Arguments = arguments,
            GitDirectory = gitDirectory,
            Credential = context.Credential,
            Timeout = remaining,
        };
        var result = onLine is null
            ? await git.RunAsync(invocation, context.Aborted)
            : await git.RunLinesAsync(invocation, onLine, context.Aborted);
        return result.TimedOut ? null : result;
    }

    private FetchResponse Respond(RunContext context, string outcome, string reason)
    {
        logger.LogInformation("Mirror {Repo}: {Outcome} ({Reason}) after {Elapsed} ms", context.Key, outcome, reason, context.Clock.ElapsedMilliseconds);
        return new FetchResponse(outcome, reason, 0, null, 0, SafeFreeBytes(), context.Clock.ElapsedMilliseconds);
    }

    private long SafeFreeBytes()
    {
        try
        {
            return disk.FreeBytes;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Maps git's (already scrubbed) stderr onto the contract's reasons.</summary>
    internal static string Classify(GitResult result)
    {
        var error = result.StandardError.ToLowerInvariant();
        if (error.Contains("authentication failed") || error.Contains("could not read username") || error.Contains("could not read password")
            || error.Contains(" 401") || error.Contains(" 403") || error.Contains("access denied") || error.Contains("permission denied"))
            return FetchReasons.Auth;
        if (error.Contains("not found") || error.Contains(" 404") || error.Contains("does not appear to be a git repository"))
            return FetchReasons.NotFound;
        return FetchReasons.Error;
    }
}
