using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Converts FileCoverage.Branches from a flat list of edges
/// ({ Line, BlockId, BranchId, Taken }) to one entry per line
/// ({ Line, Arity, TakenArms, Floor }), and drops the now-meaningless
/// BranchFormat stamp.
/// </summary>
/// <remarks>
/// <para>
/// The conversion is exact for documents the old code stamped "lcov": those
/// block/branch ids are real arm identities, so they become TakenArms directly.
/// For Cobertura- and JaCoCo-stamped documents the stored ids were positional
/// fiction — the parser minted "0"/0, "0"/1 … to represent a bare (covered/total)
/// count — so they become a Floor with an empty arm set, which is exactly what
/// those formats actually told us. Nothing is invented and nothing is lost that
/// the documents still held.
/// </para>
/// <para>
/// Measured against production 2026-09-18: 200,230 FileCoverage documents, 96%
/// of the database, but only ~25% carry branch data at all — the rest return on
/// the first line. A full read of the collection takes 7 seconds, against a
/// deploy readiness budget of 180s, so a single-shot patch fits with room to
/// spare. It runs blocking inside UseSpark() before the port opens.
/// </para>
/// </remarks>
public partial class M_202609190900_BranchesBecomePerLineArmSets : ISparkMigration
{
    public static long Version => 202609190900;
    public static string? Description => "Branch coverage becomes a per-line arm set plus a floor";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // ⚠️ The already-migrated check is INSIDE the script, not a where clause.
                // PatchByQueryOperation does not wait for non-stale results, so filtering on a
                // field would run against an index that may not have caught up -- and a stale
                // index matches nothing, which looks exactly like "there was nothing to
                // migrate". A collection scan with no where clause needs no index at all.
                //
                // The shape test is on the first element rather than on BranchFormat, because
                // a document can carry edges with no stamp (the old code only stamped when it
                // saw branch data) and because it makes the script idempotent by construction:
                // once converted, the elements have no BranchId and it returns.
                Query = """
                    from FileCoverages as f
                    update {
                        var wasLcov = f.BranchFormat === 'lcov';
                        delete f.BranchFormat;

                        if (!f.Branches || f.Branches.length === 0) { return; }
                        if (f.Branches[0].BranchId === undefined) { return; }

                        var byLine = {};
                        for (var i = 0; i < f.Branches.length; i++) {
                            var edge = f.Branches[i];
                            var line = byLine[edge.Line];
                            if (!line) {
                                line = { Line: edge.Line, Arity: 0, TakenArms: [], Floor: 0 };
                                byLine[edge.Line] = line;
                            }
                            line.Arity++;
                            if (edge.Taken > 0) {
                                if (wasLcov) {
                                    line.TakenArms.push(edge.BlockId + ':' + edge.BranchId);
                                } else {
                                    line.Floor++;
                                }
                            }
                        }

                        var lines = [];
                        for (var key in byLine) {
                            var entry = byLine[key];
                            entry.TakenArms.sort();
                            lines.push(entry);
                        }
                        lines.sort(function (a, b) { return a.Line - b.Line; });
                        f.Branches = lines;
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
