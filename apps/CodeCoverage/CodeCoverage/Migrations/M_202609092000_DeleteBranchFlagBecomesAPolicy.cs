using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Turns the stored boolean <c>Repository.DeleteBranchOnPrClose</c> into the three-state
/// <c>EDeleteBranchPolicy</c>, so a repository that never expressed an opinion inherits its
/// account's default instead of being pinned to "off".
/// </summary>
/// <remarks>
/// ⚠️ <b>Every repository currently carries an explicit <c>false</c>, and none of them meant it.</b>
/// Repository documents are stored whole (<c>session.StoreAsync(repository, id, ct)</c>), so the
/// non-nullable <c>bool</c> introduced in #382 serialized on every write — the value records that
/// the property existed, not that anyone chose it. Deserialized onto the new enum a stored
/// <c>false</c> becomes <c>Inherit</c> (0) by luck of the ordinal, but a stored <c>true</c> would
/// also become <c>Inherit</c>, silently discarding the one opinion anybody expressed. This migration
/// makes both cases explicit rather than relying on that coincidence.
///
/// <para>
/// <b>true → Enabled, everything else → Inherit.</b> A repository that was switched on keeps
/// deleting branches; one that was never touched defers to its account, which is the whole point of
/// the change. Nothing becomes <c>Disabled</c>: that state means "override my account and do not
/// delete", and no stored document can have meant it, because there was no account setting to
/// override.
/// </para>
///
/// <para>
/// Measured before writing: <c>from Repositories where DeleteBranchOnPrClose = true</c>. The flag
/// had lived on <c>Repository</c> for about a day and had no writer at all — no Edit right, no
/// controller, no client code — so the expected count is zero. The <c>true</c> arm is here because
/// "expected zero" is not "measured zero" on a production database, and because a restored backup or
/// a hand-edited document could carry one.
/// </para>
///
/// <para>
/// Idempotent: the second run sees string values, and neither arm matches a string, so it is a
/// no-op. Safe on a replay, a restored backup, or a fresh environment.
/// </para>
/// </remarks>
public partial class M_202609092000_DeleteBranchFlagBecomesAPolicy : ISparkMigration
{
    public static long Version => 202609092000;

    public static string? Description =>
        "Repository.DeleteBranchOnPrClose becomes a three-state policy; an untouched repository inherits its account";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // RQL strings rather than typed references, deliberately — the same reasoning as
                // M_202609091200: a migration is a historical fact about a database at a moment in
                // time, and naming the enum as a symbol would let a later rename silently change
                // what an already-applied migration meant.
                Query = """
                    from Repositories as r update {
                        if (r.DeleteBranchOnPrClose === true) {
                            r.DeleteBranchOnPrClose = "Enabled";
                        } else if (r.DeleteBranchOnPrClose === false) {
                            r.DeleteBranchOnPrClose = "Inherit";
                        }
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
