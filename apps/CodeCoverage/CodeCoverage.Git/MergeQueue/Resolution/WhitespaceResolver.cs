using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>whitespace</c> (#657, docs/coverage_file_formatter_PRD.md D10, D17): a conflict whose every region differs between
/// the two sides only in whitespace takes the pull request's side, as <c>git merge-file --theirs</c> would.
/// <list type="number">
/// <item><b>The pre-filter:</b> the plain diff3 regions of <c>git merge-file --diff3 --diff-algorithm=myers</c>; each
/// region's base-branch lines and pull-request lines must be equal once every whitespace character is removed.</item>
/// <item><b>Refused:</b> no merge base (add/add); a side that is not strict UTF-8 or has a marker-like line; any region that
/// differs in anything but whitespace.</item>
/// <item><b>Output:</b> the common text, and per region the pull request's lines.</item>
/// </list>
/// The pre-filter is not the proof: whitespace inside a string literal is content. The resolution always regenerates, and
/// the repository's <c>formatting.yml</c> (pipeline mode) rebuilds both whole-file resolutions, formats them with the base
/// branch's formatter and requires them byte-identical; otherwise the run fails and the pull request is evicted (D17).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class WhitespaceResolver : IConflictResolver
{
    /// <summary>The cap on a side and on the merged output (§10.6).</summary>
    public const long MaxBytes = ResolutionLimits.MaxParsedBytes;

    public string Strategy => MergeQueueWireRules.Whitespace;

    /// <summary>D17: the head's side stands only once the formatting run has proven it.</summary>
    public bool RegeneratesByDefault => true;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(1) && stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        if (!input.Stages.TryGetValue(1, out var @base))
            return Refuse("Both sides added the file (an add/add conflict): there is no merge base to compare the sides against.");

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
        if (document.Regions.Any(r => !EqualWithoutWhitespace(r.Ours, r.Theirs)))
            return Refuse("A conflict changes more than whitespace.");

        return new ConflictResolution.Content(Theirs(document));
    }

    /// <summary>Both line lists' text with every whitespace character removed is the same.</summary>
    internal static bool EqualWithoutWhitespace(IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
        => string.Equals(Squeeze(ours), Squeeze(theirs), StringComparison.Ordinal);

    private static string Squeeze(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
            foreach (var c in line)
                if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    /// <summary>The common text, and per region the pull request's lines: <c>merge-file --theirs</c>.</summary>
    internal static string Theirs(Diff3Document document)
    {
        var result = new StringBuilder();
        foreach (var piece in document.Pieces)
        {
            var lines = piece switch
            {
                Diff3Piece.Common common => common.Lines,
                Diff3Piece.Region region => region.Theirs,
                _ => [],
            };
            foreach (var line in lines)
                result.Append(line);
        }
        return result.ToString();
    }

    private static ConflictResolution.Refused Refuse(string detail) => new(EvictionReason.ResolverRefused, detail);
}
