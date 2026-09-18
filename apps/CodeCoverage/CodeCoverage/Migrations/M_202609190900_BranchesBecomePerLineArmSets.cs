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
/// The conversion is exact for documents stamped "lcov": those block/branch ids
/// are real arm identities, so they become TakenArms directly. Cobertura- and
/// JaCoCo-stamped documents become a Floor with an empty arm set, because their
/// stored ids were positional fiction — the parser minted "0"/0, "0"/1 … to
/// represent a bare (covered/total) count. That is exactly what those formats
/// reported; inventing arm keys for them would claim coverage never measured.
/// </para>
/// <para>
/// ⚠️ <b>IgnoreMaxStepsForScript is load-bearing, not a precaution.</b> A patch
/// script is capped at 10,000 statements per document
/// (<c>Patching.MaxStepsForScript</c>) and this one walks every edge. Measured
/// against a copy of production: 421 edges converts, 5,263 faults, so the budget
/// runs out around 450 edges — and production holds roughly 770 documents above
/// 400 edges, ten of them at 5,263. Without this option the operation faults,
/// <see cref="UpAsync"/> throws, startup aborts and the deploy fails with the
/// site unavailable. Making the loop cheaper does not rescue it either: at ~24
/// statements per edge the largest document needs ~125,000. The option is scoped
/// to this one operation, so no server-wide patching limit is relaxed.
/// </para>
/// <para>
/// Measured against production 2026-09-19: 201,698 FileCoverage documents (96%
/// of the database) of which 108,648 carry branch data — 16,203 lcov-stamped and
/// 92,445 cobertura-stamped, none JaCoCo. A full read of the collection takes 7
/// seconds against a 180s deploy readiness budget, and the script returns on its
/// third line for the 93,050 documents with no branches.
/// </para>
/// <para>
/// Re-running is safe: a converted document has no BranchId on its first entry
/// and returns before any write. And should this migration never run at all,
/// nothing breaks — <see cref="Services.LegacyBranchCompatibility"/> performs the
/// same derivation as documents are loaded, so reports stay readable either way.
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
            new PatchByQueryOperation(
                new IndexQuery
                {
                    // ⚠️ The already-migrated check is INSIDE the script, not a where clause.
                    // PatchByQueryOperation does not wait for non-stale results, so filtering on a
                    // field would run against an index that may not have caught up -- and a stale
                    // index matches nothing, which looks exactly like "there was nothing to
                    // migrate". A collection scan with no where clause needs no index at all.
                    //
                    // The shape test is on the first element rather than on BranchFormat, because
                    // a document can carry edges with no stamp and because it makes the script
                    // idempotent by construction: once converted, the entries have no BranchId.
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
                },
                new QueryOperationOptions { IgnoreMaxStepsForScript = true }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
