using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Resolves a build's base and, for partial builds, its scoped/projected
/// numbers — the one computation behind both the status endpoint and the
/// check-run publisher, so a poller and a check can never disagree.
/// </summary>
public static class BuildComparer
{
    /// <param name="Base">How the base resolved (never null — <see cref="ResolvedBase.None"/> is the no-base answer).</param>
    /// <param name="Partial">Scoped baseline + projection; null until the head build has a tree summary (finalized) and a base resolved.</param>
    /// <param name="IncompleteReasons">Why the projection is best-effort; empty means complete.</param>
    public sealed record Result(ResolvedBase Base, PartialComparison.Result? Partial, string[] IncompleteReasons);

    public static async Task<Result> CompareAsync(
        IAsyncDocumentSession session, IBaseResolver baseResolver,
        Repository repository, Build build, Commit? commit, CancellationToken cancellationToken)
    {
        var resolved = commit is null
            ? new ResolvedBase(build.DeclaredBaseSha, null, ResolvedBase.None, null, null)
            : await baseResolver.ResolveAsync(repository, commit, build.DeclaredBaseSha, cancellationToken);

        if (resolved.BaseBuildId is null || build.Id is null)
            return new Result(resolved, null, []);

        var headTree = await session.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(build.Id), cancellationToken);
        if (headTree is null)
            return new Result(resolved, null, []);

        var baseTree = await session.LoadAsync<BuildTreeSummary>(BuildTreeSummary.DocumentId(resolved.BaseBuildId), cancellationToken);
        if (baseTree is null)
        {
            // Deleted between the resolver's existence check and this load —
            // the same degradation as never having resolved.
            return new Result(resolved with { ResolvedSha = null, Mode = ResolvedBase.None, BaseBuildId = null, Coverage = null }, null, []);
        }

        var fileList = await ReadHeadFileList(session, build, cancellationToken);
        var comparison = PartialComparison.Compute(headTree, baseTree, fileList);

        var reasons = new List<string>();
        if (resolved.Mode != ResolvedBase.Exact)
            reasons.Add("baseWalked");
        if (fileList is null)
            reasons.Add("noFileList");
        if (headTree.Files.Any(f => !f.Matched))
            reasons.Add("unmatchedPaths");
        if (Build.ClassifyState(build) == "CompleteWithErrors")
            reasons.Add("parseErrors");

        return new Result(resolved, comparison, [.. reasons]);
    }

    /// <summary>
    /// The head's git file list, needed to prune PR-deleted files from the
    /// projection. Every job uploads the same `git ls-files` of the same
    /// commit, so the first session that attached one wins.
    /// </summary>
    private static async Task<string[]?> ReadHeadFileList(IAsyncDocumentSession session, Build build, CancellationToken cancellationToken)
    {
        foreach (var buildSession in build.Sessions)
        {
            var attachment = await session.Advanced.Attachments.GetAsync(
                build, UploadAttachments.FileListName(buildSession.SessionId), cancellationToken);
            if (attachment is null)
                continue;

            await using var stream = attachment.Stream;
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return null;
    }
}
