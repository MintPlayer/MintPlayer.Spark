using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Re-keys the three id-valued reference fields that <c>M_202609210900_ForgeQualifiedDocumentIds</c>
/// left pointing at documents it then deleted.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>These are dangling references on production, not a theoretical gap.</b> The first pass
/// re-keyed ids and rewrote references in the same script — but only the references it enumerated.
/// Three were missed, and phase 4 then deleted the documents they point at:
/// </para>
/// <list type="number">
/// <item><description>
/// <b><c>PullRequestFeedback.Repository</c></b> — the <c>PullRequestFeedbacks</c> script re-keys
/// <c>id(d)</c> and nothing else, so every feedback record still points at <c>Repositories/1234</c>.
/// </description></item>
/// <item><description>
/// <b><c>FileCoverage.Origin.FromBuildId</c></b> — the <c>FileCoverages</c> script rewrites
/// <c>d.BuildId</c> only. <c>Origin</c> is the carry-forward provenance, written on every file
/// copied from a base commit, so this is the widest of the three.
/// </description></item>
/// <item><description>
/// <b><c>GitHubProject.Account</c></b> — <c>GitHubProjects</c> appears in <b>no phase</b> of the
/// first migration. Its own ids are node-id-derived and correctly do not move, which is exactly why
/// the collection was skipped — but its <c>Account</c> reference still had to follow.
/// </description></item>
/// </list>
/// <para>
/// <b>Why none of this failed loudly.</b> A dangling reference is not an error in RavenDB; a
/// <c>LoadAsync</c> on a missing id returns null. So the symptom is an empty panel, a missing
/// avatar, a comment that re-posts instead of editing — each of which reads as a different,
/// smaller bug. The first migration's own guard could not have caught it either: it counts ids and
/// nothing else, so every reference field could be wrong and it would still pass.
/// </para>
/// <para>
/// ⚠️ <b>Idempotent, like the first pass, and for the same reason.</b> <c>qualify</c> returns its
/// input unchanged when the value already carries the segment or does not start with the collection
/// prefix, so a re-run is a no-op and a partially-applied run completes. That matters more here
/// than it did there: this runs against data the first migration has already half-converted.
/// </para>
/// <para>
/// This repairs references only. It creates nothing, deletes nothing, and re-keys no ids — so
/// unlike its predecessor it has no phase ordering to get wrong.
/// </para>
/// </remarks>
public partial class M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed : ISparkMigration
{
    public static long Version => 202609220900;
    public static string? Description => "Reference fields the forge-qualifying pass missed";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609220900_ForgeQualifiedReferencesTheFirstPassMissed> logger;

    /// <summary>
    /// The same helper the first pass used, so the two agree by construction about what a qualified
    /// id looks like. Copied rather than shared: a migration is a record of what ran, and one that
    /// changes meaning when a helper is edited later is not.
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
        await PatchAsync("PullRequestFeedbacks.Repository", $$"""
            from PullRequestFeedbacks as d update {
                {{QualifyFunction}}
                d.Repository = qualify(d.Repository, 'Repositories');
            }
            """, cancellationToken);

        // ⚠️ Guarded on `d.Origin` being present: a measured file has no carry-forward provenance,
        // and assigning through a null would fail the whole patch for that document rather than
        // skipping it.
        await PatchAsync("FileCoverages.Origin.FromBuildId", $$"""
            from FileCoverages as d update {
                {{QualifyFunction}}
                if (d.Origin) {
                    d.Origin.FromBuildId = qualify(d.Origin.FromBuildId, 'Commits');
                }
            }
            """, cancellationToken);

        await PatchAsync("GitHubProjects.Account", $$"""
            from GitHubProjects as d update {
                {{QualifyFunction}}
                d.Account = qualify(d.Account, 'Accounts');
            }
            """, cancellationToken);
    }

    private async Task PatchAsync(string label, string script, CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = script }), token: cancellationToken);
        var result = await operation.WaitForCompletionAsync<BulkOperationResult>();

        // ⚠️ "scanned", not "repaired" — the same caveat the first pass records. The patch matches
        // every document in the collection and `qualify` no-ops the ones already correct, so this
        // number is the collection size on both the first run and every re-run. It is logged
        // because a count of ZERO is the informative case: it means the collection is empty or the
        // name is wrong, and those are worth noticing.
        logger.LogInformation(
            "Re-qualified references in {Field}: {Count} documents scanned.", label, result.Total);
    }
}
