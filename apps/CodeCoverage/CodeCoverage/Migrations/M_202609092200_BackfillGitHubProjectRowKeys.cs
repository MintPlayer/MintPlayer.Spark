using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Gives every stored <c>GitHubProject.EventMappings</c> and <c>.Columns</c> row a key, so the
/// startup gate's own remedy exists.
/// </summary>
/// <remarks>
/// <c>M_202609091200</c> deliberately skipped both collections, on the grounds that they "already
/// carry meaningful keys … and production has zero keyless rows in either (measured)". The
/// measurement was right about production and says nothing about anywhere else, while
/// <c>ValueObjectKeyVerifier</c> is unconditional: any environment holding one keyless row cannot
/// start at all.
///
/// <para>
/// That is not hypothetical. A development database hit it on 2026-09-09 with a single
/// <c>EventMappings</c> row whose <c>Id</c> was <c>""</c> — written before
/// <c>GitHubProjectActions.OnBeforeSaveAsync</c> began deriving the key — and the process refused to
/// boot with the message "Run the backfill migration for these collections before starting."
/// <b>No such migration existed.</b> A gate whose remedy does not exist is a gate that cannot be
/// cleared: a dev box, a restored backup, or a fresh environment seeded from older data is simply
/// stuck.
/// </para>
///
/// <para>
/// The key is <em>derived, not synthesized</em>, matching what the application itself assigns:
/// <c>EventColumnMapping.Id = EventType</c> (<c>GitHubProjectActions.OnBeforeSaveAsync</c>), and a
/// <c>ProjectColumn</c>'s id is the GraphQL single-select option id it already carries. Stamping a
/// synthetic guid over either would be a regression rather than a backfill — the same reasoning
/// <c>M_202609091200</c> gives for leaving them alone, which is why this fills only what is empty.
/// </para>
///
/// <para>
/// ⚠️ A row that is keyless <em>and</em> has no value to derive from is left alone deliberately. It
/// cannot be given a meaningful key, and a synthetic one would silently become that row's identity
/// forever. The gate will still refuse to start, which is the correct outcome: that row needs a
/// person to look at it, not a migration to paper over it.
/// </para>
///
/// <para>
/// Idempotent: <c>if (!row.Id)</c> preserves every key already present, so a replay leaves the
/// change vector untouched.
/// </para>
/// </remarks>
public partial class M_202609092200_BackfillGitHubProjectRowKeys : ISparkMigration
{
    public static long Version => 202609092200;

    public static string? Description =>
        "Give stored GitHubProject EventMappings/Columns rows their derived key";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // RQL strings, never typed references — a migration is a historical fact about the
                // database, and a later rename must not change what an applied migration meant.
                //
                // `if (rows)` is load-bearing twice over: either collection may be absent from an
                // older document entirely.
                Query = """
                    from GitHubProjects as p update {
                        var mappings = p.EventMappings;
                        if (mappings) {
                            for (var i = 0; i < mappings.length; i++) {
                                if (!mappings[i].Id && mappings[i].EventType) {
                                    mappings[i].Id = mappings[i].EventType;
                                }
                            }
                        }

                        var columns = p.Columns;
                        if (columns) {
                            for (var j = 0; j < columns.length; j++) {
                                if (!columns[j].Id && columns[j].Name) {
                                    columns[j].Id = columns[j].Name;
                                }
                            }
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
