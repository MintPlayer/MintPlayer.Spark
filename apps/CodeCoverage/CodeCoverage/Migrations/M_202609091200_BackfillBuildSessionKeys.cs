using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Gives every stored <c>BuildSession</c> a row key, which the type gained only now.
///
/// An embedded row needs a stable key so a save can match it against the stored collection —
/// that is what lets a row be populated onto its stored instance rather than a fresh one, and
/// what makes the row type's New/Edit/Delete rights decidable. The key is minted by a field
/// initializer, so every row created from here on has one; every row created before this does
/// not.
///
/// ⚠️ A keyless row cannot be recognised once it is loaded. The initializer runs during
/// deserialization and there is no stored value to overwrite it, so the row comes back carrying a
/// brand-new guid — a different one on every load. Two loads of the same untouched document
/// disagree about row identity, and any in-process check for a missing key is unreachable. Worse,
/// merely loading such a document marks it dirty, so an unrelated SaveChanges writes random ids
/// into a Build nobody edited. Measured; pinned by NestedRowIdentityTests.
///
/// That is why this runs before the property can do any harm, and why a startup gate refuses to
/// serve requests while any keyless row remains.
///
/// Measured against production 2026-09-09: 261 of 261 Builds carry keyless Sessions rows, i.e.
/// every one, because Sessions never had an Id.
/// </summary>
/// <remarks>
/// ⚠️ <b>Only Sessions.</b> <c>GitHubProject.Columns</c> and <c>.EventMappings</c> are also
/// <c>[ValueObject]</c> collections, and are deliberately left alone: both already carry meaningful
/// keys — a GraphQL single-select option id, and a value re-derived from the event type in
/// <c>GitHubProjectActions.OnBeforeSaveAsync</c> — and production has zero keyless rows in either
/// (measured). Stamping a synthetic key over a meaningful one would be a regression, not a backfill.
/// <para>
/// ⚠️ <b>RQL strings, never typed references — deliberately.</b> A migration is a historical fact
/// about a database at a moment in time; an entity class is a live thing that keeps changing. If
/// this named <c>BuildSession</c> or <c>Sessions</c> as symbols, then renaming or deleting either
/// later would break the build of a migration that has already run everywhere and must never run
/// differently. Worse than a broken build, a rename that still compiles would silently change what
/// an already-applied migration means.
/// <para>
/// So the collection and property names are strings, frozen at the shape the database had when this
/// was written. The cost is that a typo is not caught by the compiler; the test suite and the
/// startup gate are what catch it instead.
/// </para>
/// </remarks>
public partial class M_202609091200_BackfillBuildSessionKeys : ISparkMigration
{
    public static long Version => 202609091200;
    public static string? Description => "Give stored BuildSession rows a key";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // ⚠️ RavenDB's patch engine has NO guid helper — newGuid, raven, crypto and uuid are
                // all undefined (measured on 7.1.1). So the key is derived rather than generated:
                // the document id is unique, the array index is unique within the document, and the
                // pair is therefore unique and — unlike Math.random() — reproducible.
                //
                // Nothing constrains the key's shape: SparkValueObjects stores it as an opaque
                // string. It does not have to look like the guid the initializer mints.
                //
                // `if (rows)` is load-bearing: Sessions may be absent from an old document
                // entirely. `if (!rows[i].Id)` makes the patch idempotent and preserves any key
                // that is already there — verified by replaying it, which left the change vector
                // untouched.
                Query = """
                    from Builds as b update {
                        var rows = b.Sessions;
                        if (rows) {
                            for (var i = 0; i < rows.length; i++) {
                                if (!rows[i].Id) {
                                    rows[i].Id = id(b).replace(/[^A-Za-z0-9]/g, '') + i.toString();
                                }
                            }
                        }
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the
        // next start rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
