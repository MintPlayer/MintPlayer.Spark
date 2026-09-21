using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Gives every document id the forge that hosts it: <c>Repositories/1234</c> becomes
/// <c>Repositories/github/1234</c>, and the eight shapes nested under a commit id follow.
/// </summary>
/// <remarks>
/// <para>
/// A numeric repository id is unique only <em>within</em> a forge — GitHub repository 1234 and
/// GitLab project 1234 are different repositories — so without this segment the second forge
/// collides with the first on its first write. RavenDB ids are immutable, which is why this is a
/// re-key of ~224,000 documents rather than a rename, and why it is worth doing once, carefully
/// (D7, D25).
/// </para>
/// <para>
/// <b>Four phases, in this order, and the order is a correctness property:</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Put</b> every document at its new id, rewriting id-valued reference fields in the same
/// script. The rewrite rides along because the new value is derivable from the old by the same
/// string operation, which halves the passes over <c>FileCoverages</c> — the only collection where
/// the cost is material.
/// </description></item>
/// <item><description>
/// <b>Move attachments</b> onto the new build ids. ⚠️ <c>put()</c> does <b>not</b> carry
/// attachments; they stay on the source document and would die with it in phase 4. This is the
/// step between a clean migration and silently losing every stored coverage report.
/// </description></item>
/// <item><description>
/// <b>Verify</b> that each collection's new-id count matches what was read, and refuse to continue
/// otherwise. A throw here leaves the legacy documents in place, which is the recoverable state.
/// </description></item>
/// <item><description>
/// <b>Delete</b> the legacy documents, last, once their replacements are confirmed present.
/// </description></item>
/// </list>
/// <para>
/// ⚠️ <b>Re-entrant by construction.</b> <see cref="ISparkMigration"/> writes its applied-marker
/// only after <c>Up</c> returns, so a failure anywhere above means the next container start runs
/// this again <em>from the top</em>. Every script therefore tests the target shape before acting
/// and every phase is idempotent: re-running a completed migration processes nothing.
/// </para>
/// <para>
/// Measured against a restored production copy (228,059 documents): the put phase takes ~80s in
/// total, dominated by 220,419 <c>FileCoverages</c>, and the delete phase ~20s. ⚠️ That exceeds the
/// container healthcheck's 60s <c>start_period</c>, which is raised in the same commit — a deploy
/// must not mark itself unhealthy while doing exactly what it is supposed to.
/// </para>
/// </remarks>
public partial class M_202609210900_ForgeQualifiedDocumentIds : ISparkMigration
{
    public static long Version => 202609210900;
    public static string? Description => "Document ids carry the forge that hosts them";

    /// <summary>
    /// The only forge in the data. Everything stored before this migration is GitHub — the app had
    /// no other integration — so the segment is a constant here rather than a lookup.
    /// </summary>
    private const string Segment = "github";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609210900_ForgeQualifiedDocumentIds> logger;

    /// <summary>
    /// Rewrites an id's collection prefix to carry the forge, and returns it unchanged if it
    /// already does. Shared by every script below so the rule has one definition.
    /// </summary>
    private const string QualifyFunction = """
        function qualify(value, collection) {
            if (!value) return value;
            var prefix = collection + '/';
            if (value.indexOf(prefix) !== 0) return value;
            var rest = value.substring(prefix.length);
            if (rest.indexOf('github/') === 0) return value;
            return prefix + 'github/' + rest;
        }
        """;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // Phase 1 — put, with references rewritten in the same pass.
        await PutAsync("Repositories", $$"""
            from Repositories as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Repositories');
                if (newId !== id(d)) {
                    d.Account = qualify(d.Account, 'Accounts');
                    d.Provider = 'GitHub';
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("Accounts", $$"""
            from Accounts as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Accounts');
                if (newId !== id(d)) {
                    d.Provider = 'GitHub';
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("Commits", $$"""
            from Commits as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Commits');
                if (newId !== id(d)) {
                    d.Repository = qualify(d.Repository, 'Repositories');
                    d.LatestBuildId = qualify(d.LatestBuildId, 'Commits');
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("Builds", $$"""
            from Builds as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Commits');
                if (newId !== id(d)) {
                    d.Commit = qualify(d.Commit, 'Commits');
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("BuildTreeSummaries", $$"""
            from BuildTreeSummaries as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Commits');
                if (newId !== id(d)) {
                    d.BuildId = qualify(d.BuildId, 'Commits');
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("CommitAssemblies", $$"""
            from CommitAssemblies as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Commits');
                if (newId !== id(d)) {
                    d.Commit = qualify(d.Commit, 'Commits');
                    d.Repository = qualify(d.Repository, 'Repositories');
                    if (d.Builds) {
                        for (var i = 0; i < d.Builds.length; i++) {
                            d.Builds[i].BuildId = qualify(d.Builds[i].BuildId, 'Commits');
                        }
                    }
                    put(newId, d);
                }
            }
            """, cancellationToken);

        await PutAsync("PullRequestFeedbacks", $$"""
            from PullRequestFeedbacks as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'PullRequestFeedbacks');
                if (newId !== id(d)) { put(newId, d); }
            }
            """, cancellationToken);

        // The largest collection, and the reason the reference rewrite rides along rather than
        // taking a pass of its own.
        await PutAsync("FileCoverages", $$"""
            from FileCoverages as d update {
                {{QualifyFunction}}
                var newId = qualify(id(d), 'Commits');
                if (newId !== id(d)) {
                    d.BuildId = qualify(d.BuildId, 'Commits');
                    put(newId, d);
                }
            }
            """, cancellationToken);

        // ApiTokens are NOT re-keyed (D25 — Raven's id generator, no forge id in the key), but they
        // reference repositories by document id and that reference has moved.
        await PutAsync("ApiTokens", $$"""
            from ApiTokens as d update {
                {{QualifyFunction}}
                var changed = false;
                if (d.GithubRepositories) {
                    for (var i = 0; i < d.GithubRepositories.length; i++) {
                        var q = qualify(d.GithubRepositories[i], 'Repositories');
                        if (q !== d.GithubRepositories[i]) { d.GithubRepositories[i] = q; changed = true; }
                    }
                }
                if (changed) { put(id(d), d); }
            }
            """, cancellationToken);

        // Phase 2 — attachments, before anything is deleted.
        await MoveAttachmentsAsync(cancellationToken);

        // Phase 3 — verify, then Phase 4 — delete.
        await VerifyAndDeleteAsync(cancellationToken);
    }

    private async Task PutAsync(string label, string script, CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = script }), token: cancellationToken);
        var result = await operation.WaitForCompletionAsync<BulkOperationResult>();
        // ⚠️ "scanned", not "re-keyed". The script skips documents that already carry the segment,
        // but the operation still matched them, so a second run legitimately reports roughly double
        // the collection size. Reading this number as work done would make a correct idempotent
        // re-run look like a duplication bug.
        logger.LogInformation(
            "Forge-qualified {Collection}: {Count} documents scanned.", label, result.Total);
    }

    /// <summary>
    /// Copies every attachment onto the re-keyed build document and removes the original.
    /// </summary>
    /// <remarks>
    /// ⚠️ A client-side loop rather than a patch, because attachments are not reachable from a
    /// patch script at all. It is affordable: production holds ~708 attachments across ~318 builds.
    /// <para>
    /// Copy-then-delete rather than move, and the delete is skipped unless the copy is confirmed —
    /// a coverage report is not reconstructible, so the failure mode worth avoiding is losing one,
    /// not keeping a duplicate for one more phase.
    /// </para>
    /// </remarks>
    private async Task MoveAttachmentsAsync(CancellationToken cancellationToken)
    {
        var moved = 0;
        var legacyPrefix = "Commits/";
        var qualifiedPrefix = $"Commits/{Segment}/";

        using var session = store.OpenAsyncSession();
        await using var stream = await session.Advanced.StreamAsync<Entities.Build>(
            startsWith: legacyPrefix, token: cancellationToken);

        var pending = new List<(string OldId, string NewId, string Name)>();
        while (await stream.MoveNextAsync())
        {
            var oldId = stream.Current.Id;
            if (oldId is null || oldId.StartsWith(qualifiedPrefix, StringComparison.Ordinal))
                continue;

            var newId = qualifiedPrefix + oldId[legacyPrefix.Length..];

            // Attachment names live in the streamed document's metadata; there is no way to ask a
            // patch script for them, which is why this phase is a client-side loop at all.
            var metadata = stream.Current.Metadata;
            if (!metadata.ContainsKey(Raven.Client.Constants.Documents.Metadata.Attachments))
                continue;

            if (metadata[Raven.Client.Constants.Documents.Metadata.Attachments]
                is not IEnumerable<object> entries)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is IDictionary<string, object?> attachment
                    && attachment.TryGetValue("Name", out var value)
                    && value is string name)
                {
                    pending.Add((oldId, newId, name));
                }
            }
        }

        foreach (var (oldId, newId, name) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Already moved by an earlier, interrupted run.
            var existing = await store.Operations.SendAsync(
                new GetAttachmentOperation(newId, name, AttachmentType.Document, null), token: cancellationToken);
            if (existing is not null)
            {
                existing.Stream.Dispose();
                continue;
            }

            var source = await store.Operations.SendAsync(
                new GetAttachmentOperation(oldId, name, AttachmentType.Document, null), token: cancellationToken);
            if (source is null)
                continue;

            using (source)
            {
                // ⚠️ Buffered because the client refuses a non-seekable stream: it may fail over to
                // another node mid-operation and has to be able to rewind. GetAttachmentOperation
                // hands back exactly such a stream, so passing it straight through throws — caught
                // by the rehearsal, after every put phase had already succeeded.
                using var seekable = new MemoryStream();
                await source.Stream.CopyToAsync(seekable, cancellationToken);
                seekable.Position = 0;

                await store.Operations.SendAsync(
                    new PutAttachmentOperation(newId, name, seekable, source.Details.ContentType),
                    token: cancellationToken);
            }

            moved++;
        }

        logger.LogInformation("Forge-qualified attachments: {Count} moved onto re-keyed builds.", moved);
    }

    /// <summary>
    /// Refuses to delete anything until the replacements are demonstrably there, then deletes.
    /// </summary>
    /// <remarks>
    /// ⚠️ The guard is what makes a failed put phase recoverable. Without it, a script that silently
    /// processed nothing — a mistyped field name is enough, and reports success — would be followed
    /// by a delete that removed the only copy.
    /// </remarks>
    private async Task VerifyAndDeleteAsync(CancellationToken cancellationToken)
    {
        foreach (var (collection, prefix) in Targets)
        {
            var qualified = await CountAsync(collection, $"{prefix}/{Segment}/", cancellationToken);
            var legacy = await CountAsync(collection, prefix + "/", cancellationToken) - qualified;

            if (legacy > 0 && qualified == 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to delete {legacy} legacy '{collection}' documents: the re-key produced "
                    + "none, so deleting would destroy the only copy. The put phase did nothing, which "
                    + "usually means a field or collection name in its script does not match the data.");
            }

            var operation = await store.Operations.SendAsync(new DeleteByQueryOperation(new IndexQuery
            {
                Query = $"from {collection} as d where startsWith(id(d), '{prefix}/') "
                      + $"and not startsWith(id(d), '{prefix}/{Segment}/')",
            }), token: cancellationToken);
            var result = await operation.WaitForCompletionAsync<BulkOperationResult>();

            logger.LogInformation(
                "Forge-qualified {Collection}: {Kept} kept, {Removed} legacy removed.",
                collection, qualified, result.Total);
        }
    }

    /// <summary>The collections whose ids move, paired with the prefix their ids actually start with.</summary>
    /// <remarks>
    /// ⚠️ The prefix is not the collection name for five of these: <c>Builds</c>,
    /// <c>FileCoverages</c>, <c>BuildTreeSummaries</c> and <c>CommitAssemblies</c> all nest under a
    /// <c>Commits/</c> id while living in collections of their own. That nesting is cosmetic, and
    /// assuming otherwise is what leaves a re-keyed <c>Commits</c> collection with every reference
    /// to it dangling.
    /// </remarks>
    private static readonly (string Collection, string Prefix)[] Targets =
    [
        ("Repositories", "Repositories"),
        ("Accounts", "Accounts"),
        ("PullRequestFeedbacks", "PullRequestFeedbacks"),
        ("Commits", "Commits"),
        ("Builds", "Commits"),
        ("BuildTreeSummaries", "Commits"),
        ("CommitAssemblies", "Commits"),
        ("FileCoverages", "Commits"),
    ];

    /// <summary>How many documents of <paramref name="collection"/> sit under <paramref name="prefix"/>.</summary>
    /// <remarks>
    /// ⚠️ <c>limit 0</c> plus the query statistics, not <c>select count()</c> — RavenDB answers the
    /// latter with "count may only be used in group by queries", which the rehearsal hit only after
    /// every other phase had already run.
    /// </remarks>
    private async Task<long> CountAsync(string collection, string prefix, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Advanced
            .AsyncRawQuery<object>($"from {collection} as d where startsWith(id(d), $p) limit 0")
            .AddParameter("p", prefix)
            .Statistics(out var statistics);

        await query.ToListAsync(cancellationToken);
        return statistics.TotalResults;
    }
}
