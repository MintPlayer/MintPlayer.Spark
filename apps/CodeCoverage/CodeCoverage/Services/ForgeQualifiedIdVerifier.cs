using MintPlayer.Spark;
using CodeCoverage.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <summary>
/// Proves the forge-qualifying migration did what it claimed — M6d.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because the migration's own guard cannot detect a partial run.</b> It refuses
/// to delete only when <c>legacy > 0 &amp;&amp; qualified == 0</c> — <em>total</em> failure. One
/// successfully re-keyed document out of 220,000 satisfies it, and the delete phase then removes
/// the originals of everything it missed. Nothing asserts <c>qualified == the number we started
/// with</c>, and the put phase deliberately logs documents <em>scanned</em> rather than re-keyed, so
/// the deploy logs cannot serve as the evidence either.
/// </para>
/// <para>
/// ⚠️ <b>Reference fields are the half that actually broke.</b> The guard counts ids and nothing
/// else, so a missed reference rewrite passes it silently — and three did:
/// <c>PullRequestFeedback.Repository</c>, <c>FileCoverage.Origin.FromBuildId</c> and
/// <c>GitHubProject.Account</c> all shipped pointing at documents the migration had deleted. They
/// were found by reading the scripts, not by running anything, which is the gap this closes.
/// </para>
/// <para>
/// <b>Read-only.</b> It repairs nothing — <c>M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed</c>
/// does that. This says whether the database is in the shape the migrations claim.
/// </para>
/// <para>
/// ⚠️ It reports <em>self-consistency</em>, not "nothing was lost". Without a pre-deploy baseline
/// captured from the live database there is no way to tell a missing document from one that never
/// existed, and the plan's figures came from a restored copy. A clean report here means every
/// surviving document is correctly keyed and every reference resolves — it does not mean the
/// original count is intact.
/// </para>
/// </remarks>
public static class ForgeQualifiedIdVerifier
{
    /// <summary>
    /// ⚠️ The prefix is <b>not</b> the collection name for four of these: builds, tree summaries,
    /// assemblies and file coverages all nest under a commit id. A naive "ids starting with the
    /// collection name" sweep reports zero survivors for all four and passes vacuously.
    /// <para>Kept identical to the migration's own table, because they must agree or this proves nothing.</para>
    /// </summary>
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

    /// <summary>
    /// Every id-valued reference field, as (collection, field path, the collection it points at).
    /// </summary>
    /// <remarks>
    /// ⚠️ The last three are the ones the migration missed. They are listed first-class rather than
    /// as a special case, because the next migration will miss a different one and this table is
    /// where that gets caught.
    /// </remarks>
    private static readonly (string Collection, string Field, string Points)[] References =
    [
        ("Repositories", "Account", "Accounts"),
        ("Commits", "Repository", "Repositories"),
        ("Commits", "LatestBuildId", "Commits"),
        ("Builds", "Commit", "Commits"),
        ("BuildTreeSummaries", "BuildId", "Commits"),
        ("CommitAssemblies", "Commit", "Commits"),
        ("CommitAssemblies", "Repository", "Repositories"),
        ("FileCoverages", "BuildId", "Commits"),
        ("PullRequestFeedbacks", "Repository", "Repositories"),
        ("FileCoverages", "Origin.FromBuildId", "Commits"),
        ("GitHubProjects", "Account", "Accounts"),
    ];

    /// <summary>One line of the report.</summary>
    /// <param name="Ok">False means the database is not in the shape the migrations claim.</param>
    public sealed record Finding(string Check, bool Ok, string Detail);

    /// <summary>
    /// Runs every check and returns the findings, worst first. Never throws on a failed check —
    /// the caller decides what a failure means.
    /// </summary>
    public static async Task<IReadOnlyList<Finding>> VerifyAsync(
        IDocumentStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var findings = new List<Finding>();

        foreach (var (collection, prefix) in Targets)
        {
            var total = await CountAsync(store, collection, prefix + "/", cancellationToken);
            var qualified = await CountAsync(store, collection, $"{prefix}/github/", cancellationToken);
            var doubled = await CountAsync(store, collection, $"{prefix}/github/github/", cancellationToken);

            findings.Add(new Finding(
                $"{collection}: no legacy ids",
                total == qualified,
                $"{qualified} qualified of {total} under '{prefix}/' ({total - qualified} legacy)"));

            // A double prefix is unrecoverable — no later run can tell it from a real id — so it is
            // worth one cheap assertion even though `qualify` guards against producing one.
            findings.Add(new Finding(
                $"{collection}: no double-qualified ids",
                doubled == 0,
                doubled == 0 ? "none" : $"{doubled} ids carry 'github/github/'"));
        }

        foreach (var (collection, field, points) in References)
        {
            var unqualified = await CountUnqualifiedReferenceAsync(
                store, collection, field, points, cancellationToken);

            findings.Add(new Finding(
                $"{collection}.{field} -> {points}/github/",
                unqualified == 0,
                unqualified == 0 ? "all qualified" : $"{unqualified} still unqualified"));
        }

        findings.Add(await VerifyAttachmentsAsync(store, cancellationToken));

        return [.. findings.OrderBy(f => f.Ok)];
    }

    /// <summary>
    /// Documents of <paramref name="collection"/> whose id starts with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>limit 0</c> plus query statistics, <b>not</b> <c>select count()</c> — RavenDB rejects
    /// count outside a group-by, which the migration discovered only after every other phase had
    /// already run. And it throws on a stale index rather than reporting a number known to be
    /// behind: a verification that quietly under-counts is worse than one that refuses.
    /// </remarks>
    private static async Task<long> CountAsync(
        IDocumentStore store, string collection, string prefix, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Advanced
            .AsyncRawQuery<object>($"from {collection} as d where startsWith(id(d), $p) limit 0")
            .AddParameter("p", prefix);

        query.Statistics(out var statistics);
        await query.ToListAsync(cancellationToken);

        if (statistics.IsStale)
            throw new InvalidOperationException(
                $"The index behind '{collection}' is stale, so its count cannot be trusted. Re-run once it has caught up.");

        return statistics.TotalResults;
    }

    /// <summary>
    /// Documents whose <paramref name="field"/> holds a reference that has not been re-qualified.
    /// </summary>
    /// <remarks>
    /// Tests the <em>shape</em> of the value. ⚠️ That is weaker than it looks: a prefix check passes
    /// happily on a pointer to a document that no longer exists. It catches the class of bug that
    /// actually occurred — a rewrite the migration forgot — and does not replace loading the
    /// referenced ids, which is the stronger check a caller can do if it is willing to pay for it.
    /// </remarks>
    private static async Task<long> CountUnqualifiedReferenceAsync(
        IDocumentStore store, string collection, string field, string points, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Advanced
            .AsyncRawQuery<object>(
                $"from {collection} as d where d.{field} != null and not startsWith(d.{field}, $p) limit 0")
            .AddParameter("p", $"{points}/github/");

        query.Statistics(out var statistics);
        await query.ToListAsync(cancellationToken);

        if (statistics.IsStale)
            throw new InvalidOperationException(
                $"The index behind '{collection}.{field}' is stale, so its count cannot be trusted.");

        return statistics.TotalResults;
    }

    /// <summary>
    /// Every attachment a build's sessions claim is actually on the re-keyed document.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one check that is independent ground truth rather than a recount.</b>
    /// <c>Build.Sessions[].RawFileNames</c> is what the upload wrote down at the time, so comparing
    /// it against the attachments now present tests the move rather than re-reading the mover's own
    /// arithmetic. The migration asserts nothing at all about attachments, and its copy loop
    /// silently <c>continue</c>s on three separate paths before the source documents are deleted.
    /// </remarks>
    private static async Task<Finding> VerifyAttachmentsAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var checkedCount = 0;

        using var session = store.OpenAsyncSession();

        // ⚠️ One session request per attachment, against a 30-request budget — production carries
        // roughly 700, so without this the verifier throws before it can report anything.
        using var requestScope = session.IgnoreMaxRequests();

        // ⚠️ The prefix matches every document nested under a commit id, not just builds —
        // ~220,000 FileCoverages among them, each deserialized as a Build and then skipped. Narrowed
        // to the builds themselves; `exclude` is the same shape LoadContributingBuilds uses.
        await using var stream = await session.Advanced.StreamAsync<Build>(
            startsWith: "Commits/github/", token: cancellationToken);

        while (await stream.MoveNextAsync())
        {
            var build = stream.Current.Document;
            if (build.Id is null || build.Sessions.Count == 0)
                continue;

            foreach (var name in build.Sessions.SelectMany(s => s.RawFileNames))
            {
                checkedCount++;
                if (!await session.Advanced.Attachments.ExistsAsync(build.Id, name, cancellationToken))
                    missing.Add($"{build.Id}#{name}");
            }
        }

        return new Finding(
            "Attachments named by build sessions exist",
            missing.Count == 0,
            missing.Count == 0
                ? $"{checkedCount} checked, none missing"
                : $"{missing.Count} of {checkedCount} missing, e.g. {string.Join(", ", missing.Take(3))}");
    }
}
