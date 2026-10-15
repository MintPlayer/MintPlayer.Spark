using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>keep-both</c> (O33, docs/coverage_merge_queue_PRD.md §10.4): when both sides inserted lines at the same place and
/// neither changed a line of the merge base, the result keeps the base branch's lines followed by the pull request's.
/// <list type="number">
/// <item><b>Plain diff3 regions</b> from <c>git merge-file --object-id -p --diff3 --diff-algorithm=myers</c>, never
/// merge-tree's zdiff3, which lifts lines both sides share (<c>"groupId": …</c>, <c>},</c>) out of a region and so would
/// glue two inserted five-line objects into one valid-looking object.</item>
/// <item><b>Refused:</b> no merge base (add/add); a side that is not strict UTF-8; a side with a marker-like line; any
/// region whose base section is not empty. The last one also refuses appending at the end of an array or object, since
/// the line before gains a comma.</item>
/// <item><b>Output:</b> the common text, and per region the base branch's lines, then the pull request's. Nothing is
/// folded (folding shared edge lines is what zdiff3 does); a region whose two sections are identical keeps one. Every
/// byte comes from a side, line endings included, and the result's diff against the base branch is a subset of the
/// pull request's own insertions.</item>
/// </list>
/// The merge queue then validates the content (<c>no-conflict-markers</c> and the rule's validators, inputs first) and
/// writes it with <c>hash-object</c>. A pure function of the three blobs and the writer's git, so the prepare, the
/// queue-branch push and the publish build the same tree (§0 D65).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class KeepBothResolver : IConflictResolver
{
    /// <summary>The cap on a side and on the merged output (§10.6).</summary>
    public const long MaxBytes = ResolutionLimits.MaxParsedBytes;

    public string Strategy => MergeQueueWireRules.KeepBoth;

    public bool RegeneratesByDefault => false;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        if (!input.Stages.TryGetValue(1, out var @base))
            return Refuse("Both sides added the file (an add/add conflict): there is no merge base to keep both sides against.");

        var sides = await ResolutionLimits.ReadSidesAsync(input, MaxBytes);
        if (sides.Problem is { } problem)
            return Refuse(problem);
        foreach (var (stage, text) in sides.Texts)
        {
            if (ConflictHunks.SplitLines(text).Any(l => ConflictHunks.LooksLikeMarker(l.TrimEnd('\r'))))
                return Refuse($"{ResolutionLimits.StageName(stage)} has a line that looks like a conflict marker.");
        }

        var (conflicts, output) = await input.Repository.MergeFileAsync(input.Stages[2].ObjectId, @base.ObjectId, input.Stages[3].ObjectId, MaxBytes * 3);
        if (output is null)
            return Refuse("The merged file is too large.");
        if (ConflictRepository.DecodeStrict(output) is not { } merged || Diff3Document.Parse(merged) is not { } document)
            return Refuse("The conflict regions could not be read.");
        if (conflicts == 0 && document.Regions.Any() || conflicts > 0 && !document.Regions.Any())
            return Refuse("The conflict regions do not match git's count.");

        if (document.Regions.Any(r => r.Base.Count > 0))
            return Refuse("A conflict changes lines of the merge base; only lines both sides inserted at the same place are kept both.");
        if (document.Pieces is [.., Diff3Piece.Region] && sides.Texts.Values.Any(t => t.Length > 0 && !t.EndsWith('\n')))
            return Refuse("A conflict at the end of the file, where a side has no final newline.");

        var result = new StringBuilder(merged.Length);
        foreach (var piece in document.Pieces)
        {
            switch (piece)
            {
                case Diff3Piece.Common common:
                    foreach (var line in common.Lines)
                        result.Append(line);
                    break;
                case Diff3Piece.Region region:
                    foreach (var line in region.Ours)
                        result.Append(line);
                    if (!region.Theirs.SequenceEqual(region.Ours, StringComparer.Ordinal))
                    {
                        foreach (var line in region.Theirs)
                            result.Append(line);
                    }
                    break;
            }
        }
        return new ConflictResolution.Content(result.ToString());
    }

    private static ConflictResolution.Refused Refuse(string detail) => new(EvictionReason.ResolverRefused, detail);
}

/// <summary>The caps and side reading shared by the strategies that parse (§10.6).</summary>
public static class ResolutionLimits
{
    /// <summary>A side, or a cleanly merged file, that is decoded and validated: 4 MiB.</summary>
    public const long MaxParsedBytes = 4L * 1024 * 1024;

    /// <summary>"Stage 2 (the base branch)", for eviction messages.</summary>
    public static string StageName(int stage) => stage switch
    {
        1 => "The merge base",
        2 => "The base branch's side",
        3 => "The pull request's side",
        _ => $"Stage {stage}",
    };

    /// <summary>Every stage blob of the path decoded as strict UTF-8, or the problem that stopped it.</summary>
    public static async Task<(IReadOnlyDictionary<int, string> Texts, string? Problem)> ReadSidesAsync(ConflictInput input, long maxBytes)
    {
        var texts = new SortedDictionary<int, string>();
        foreach (var (stage, entry) in input.Stages)
        {
            if (await input.Repository.ReadBytesAsync(entry.ObjectId, maxBytes) is not { } bytes)
                return (texts, $"{StageName(stage)} is over {maxBytes} bytes.");
            if (ConflictRepository.DecodeStrict(bytes) is not { } text)
                return (texts, $"{StageName(stage)} is not strict UTF-8 text.");
            texts[stage] = text;
        }
        return (texts, null);
    }
}
