using CodeCoverage.Entities;
using Raven.Client.Documents;
using Sparrow.Json;

namespace CodeCoverage.Services;

/// <summary>
/// Reads <see cref="FileCoverage"/> documents written before #420, which stored
/// branch coverage as a flat list of edges (<c>{ Line, BlockId, BranchId, Taken }</c>)
/// plus a <c>BranchFormat</c> stamp, and converts them to the per-line arm-set
/// shape as they are loaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists even though a migration converts the same documents.</b>
/// The guarantee we owe is that every report that opened before the deploy still
/// opens after it. Depending on a migration for that means depending on 200,000
/// documents converting successfully during startup, and a migration that fails
/// aborts startup — so a single unconvertible document would take the whole site
/// down rather than degrade one page. This hook removes that coupling: old
/// documents render correctly whether or not the migration has run, has finished,
/// or ever runs again.
/// </para>
/// <para>
/// The derivation is the same one the migration performs, and it is the most a
/// stored legacy document can support. lcov's block/branch ids are real arm
/// identities, so they become <see cref="LineBranchCoverage.TakenArms"/> and keep
/// their ability to union with a later report covering the other arm.
/// Cobertura's and JaCoCo's ids were positional fiction minted to represent a
/// bare (covered/total) count, so they become a <see cref="LineBranchCoverage.Floor"/>
/// with no named arms — which is exactly what those formats said in the first
/// place. Inventing arm keys for them would claim coverage that was never
/// measured.
/// </para>
/// <para>
/// Cost is a type check per loaded entity and, for a legacy document, one pass
/// over its edges. Documents already in the new shape fall out on the first
/// condition.
/// </para>
/// </remarks>
public static class LegacyBranchCompatibility
{
    /// <summary>
    /// Attach before anything loads a <see cref="FileCoverage"/>. Idempotent per
    /// store, but call it once — the handler runs for every entity conversion.
    /// </summary>
    public static void Enable(IDocumentStore store)
    {
        store.OnAfterConversionToEntity += (_, args) =>
        {
            if (args.Entity is FileCoverage file && args.Document is { } document)
                Convert(file, document);
        };
    }

    private static void Convert(FileCoverage file, BlittableJsonReaderObject document)
    {
        if (!document.TryGet<BlittableJsonReaderArray>(nameof(FileCoverage.Branches), out var edges)
            || edges is null
            || edges.Length == 0)
        {
            return;
        }

        // The shape test is the presence of BranchId rather than the absence of
        // Arity, so a document half-written by an older build still converts.
        if (edges[0] is not BlittableJsonReaderObject first || !first.TryGet("BranchId", out string? _))
            return;

        document.TryGet("BranchFormat", out string? branchFormat);
        var identifiesArms = branchFormat == "lcov";

        var byLine = new Dictionary<int, LineBranchCoverage>();
        foreach (var entry in edges)
        {
            if (entry is not BlittableJsonReaderObject edge) continue;
            if (!edge.TryGet("Line", out int line)) continue;

            if (!byLine.TryGetValue(line, out var branches))
                byLine[line] = branches = new LineBranchCoverage { Line = line };

            branches.Arity++;

            edge.TryGet("Taken", out int? taken);
            if (taken is not > 0) continue;

            if (identifiesArms)
            {
                edge.TryGet("BlockId", out string? block);
                edge.TryGet("BranchId", out string? branch);
                branches.TakenArms.Add($"{block}:{branch}");
            }
            else
            {
                branches.Floor++;
            }
        }

        foreach (var branches in byLine.Values)
            branches.TakenArms.Sort(StringComparer.Ordinal);

        file.Branches = [.. byLine.Values.OrderBy(b => b.Line)];
    }
}
