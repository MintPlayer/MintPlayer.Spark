using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>line-set</c> (docs/coverage_merge_queue_PRD.md §10.2: <c>go.sum</c>, <c>go.work.sum</c>): for a file that is a
/// sorted set of lines. Resolved only where <c>keep-both</c> would be, i.e. every conflict region is an insertion at one
/// place on both sides (an empty base section), so no line either side deleted or changed comes back. The result is the
/// whole file's non-empty lines, de-duplicated and sorted ordinally, each ended with the file's one line ending. A file
/// whose sides mix <c>\n</c> and <c>\r\n</c> is refused, as is one whose lines a set cannot hold (an empty line is
/// dropped by the set, so a side with one is refused too).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class LineSetResolver : IConflictResolver
{
    public const long MaxBytes = ResolutionLimits.MaxParsedBytes;

    public string Strategy => MergeQueueWireRules.LineSet;

    public bool RegeneratesByDefault => false;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        var (document, sides, refusal) = await KeepBothResolver.InsertionRegionsAsync(input, MaxBytes);
        if (refusal is not null)
            return refusal;

        var crlf = 0;
        var lf = 0;
        foreach (var text in sides.Values)
        {
            foreach (var line in Diff3Document.Lines(text))
            {
                if (line.EndsWith("\r\n", StringComparison.Ordinal))
                    crlf++;
                else
                    lf++;
                if (line.TrimEnd('\n').TrimEnd('\r').Length == 0)
                    return new ConflictResolution.Refused(EvictionReason.ResolverRefused, "A side has an empty line, which a line set cannot keep.");
            }
        }
        if (crlf > 0 && lf > 0)
            return new ConflictResolution.Refused(EvictionReason.ResolverRefused, "The sides mix line endings (LF and CRLF, or a last line without one).");
        var ending = crlf > 0 ? "\r\n" : "\n";

        var lines = Diff3Document.Lines(KeepBothResolver.Join(document!))
            .Select(l => l.TrimEnd('\n').TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return new ConflictResolution.Content(string.Concat(lines.Select(l => l + ending)));
    }
}
