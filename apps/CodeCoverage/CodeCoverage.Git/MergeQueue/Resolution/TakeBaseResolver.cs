using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>take-base</c> (O1, O2, O19): the base branch's version of the file (stage 2); the regenerate workflow then rewrites
/// it. A whole-file rule (<see cref="AutoResolveRule.AnyLine"/>) takes it for a conflict on any line and never reads the
/// sides; a key-scoped rule first checks that every zdiff3 conflict region of merge-tree's result lies on allow-listed
/// top-level JSON members (<see cref="ConflictHunks"/> holds the argument, D20).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class TakeBaseResolver : IConflictResolver
{
    /// <summary>A side's cap: never parsed for a whole-file rule, so a real <c>package-lock.json</c> fits (§10.6).</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>The cap on a side the key-scoped check parses as JSON, and on merge-tree's merged file.</summary>
    public const long MaxParsedBytes = ResolutionLimits.MaxParsedBytes;

    public string Strategy => MergeQueueWireRules.TakeBase;

    public bool RegeneratesByDefault => true;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        var rule = input.Rule;
        // Q7's whole-file entry: no hunk check, no JSON; the size cap still held (a side is never read whole).
        if (rule.AnyLine)
            return new ConflictResolution.UseStage(2);

        var sides = new Dictionary<int, string>();
        foreach (var (stage, entry) in input.Stages)
        {
            if (await input.Repository.ReadTextAsync(entry.ObjectId, MaxParsedBytes) is not { } text)
                return new ConflictResolution.Refused(EvictionReason.TooLarge, $"A side is over {MaxParsedBytes} bytes.");
            sides[stage] = text;
        }
        if (await input.Repository.ReadTextAsync($"{input.MergedTree}:{input.Path}", MaxParsedBytes) is not { } merged)
            return new ConflictResolution.Refused(EvictionReason.TooLarge, $"The merged file is over {MaxParsedBytes} bytes.");

        var allowed = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var (stage, text) in sides)
        {
            if (ConflictHunks.AllowedLineTexts(text, rule.Keys) is not { } set)
                return new ConflictResolution.Refused(EvictionReason.NotJson, $"Stage {stage} is not a JSON object.");
            allowed[stage] = set;
        }

        var hunks = ConflictHunks.Parse(merged);
        if (hunks is null || hunks.Count == 0)
            return new ConflictResolution.Refused(EvictionReason.HunkOutsideAllowedKeys, "The conflict regions could not be read.");
        static bool Within(IReadOnlyList<string> lines, IReadOnlySet<string>? allowed) => lines.Count == 0 || allowed is not null && lines.All(allowed.Contains);
        if (hunks.Any(h => !Within(h.Ours, allowed[2]) || !Within(h.Theirs, allowed[3]) || !Within(h.Base, allowed.GetValueOrDefault(1))))
            return new ConflictResolution.Refused(EvictionReason.HunkOutsideAllowedKeys,
                $"A conflicting hunk touches a line outside {string.Join(", ", rule.Keys.Order(StringComparer.Ordinal))}.");

        return new ConflictResolution.UseStage(2);
    }
}

/// <summary>
/// <c>evict</c>: the conflict is never resolved. In <c>coverage.yml</c> it masks a later rule or a built-in default for
/// one path (§10.3); the queue treats a path it matches like one no rule matches (O1).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class EvictResolver : IConflictResolver
{
    public string Strategy => MergeQueueWireRules.Evict;

    public bool RegeneratesByDefault => false;

    public long MaxSideBytes => 0;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule) => true;

    public Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
        => Task.FromResult<ConflictResolution>(new ConflictResolution.Refused(EvictionReason.ConflictOutsideAllowList, MergeQueueResolver.OutsideAllowListDetail));
}
