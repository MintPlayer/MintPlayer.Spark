using CodeCoverage.Git.MergeQueue.Resolution.Manifests;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// <c>keyed-merge</c> (#570, docs/coverage_backport_PRD.md §8.3): a three-way merge per key of a manifest's conflict
/// regions. The file's <see cref="IManifestFormat"/> (one service per ecosystem, injected as a list) reads each region's
/// sections into <see cref="ManifestEntry"/>s, orders versions and writes values back; the merge here is the same for
/// every format.
/// <list type="number">
/// <item><b>Regions</b> come from the same plain diff3 run as keep-both (never zdiff3), widened to whole entries
/// (<see cref="IManifestFormat.Enclose"/>). A line that is not an entry refuses the path.</item>
/// <item><b>Per key:</b> a key one side left as the merge base had takes the other side; equal values are kept once; a
/// collection entry's values merge the same way per name. A value both sides changed takes the highest only when the
/// format orders it as a version <b>and both sides raised it</b> (K1: a lowered version is a deliberate pin). Anything
/// else both sides changed, and a key removed on one side and changed on the other, refuses.</item>
/// <item><b>Output:</b> per region the base branch's entries in their order, then the pull request's new ones, the
/// convention of keep-both. Text is edited, never re-serialised: an entry keeps its side's bytes, and a merged one is the
/// base branch's text with the changed values written in. Comments and blank lines travel with the entry after them.</item>
/// </list>
/// The rule's validators run on the result (msbuild-no-duplicate-keys, json-no-duplicate-keys).
/// </summary>
[Register(typeof(IConflictResolver), ServiceLifetime.Singleton)]
public sealed class KeyedMergeResolver : IConflictResolver
{
    public const long MaxBytes = ResolutionLimits.MaxParsedBytes;

    private readonly IReadOnlyList<IManifestFormat> formats;

    public KeyedMergeResolver(IEnumerable<IManifestFormat> formats)
    {
        this.formats = [.. formats];
        if (this.formats.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != this.formats.Count)
            throw new InvalidOperationException("Two manifest formats share one name.");
    }

    public string Strategy => MergeQueueWireRules.KeyedMerge;

    public bool RegeneratesByDefault => false;

    public long MaxSideBytes => MaxBytes;

    public bool CanResolve(string path, IReadOnlyDictionary<int, MergeTreeStage> stages, AutoResolveRule rule)
        => stages.ContainsKey(2) && stages.ContainsKey(3) && stages.Values.All(e => e.Mode == "100644");

    public async Task<ConflictResolution> ResolveAsync(ConflictInput input, CancellationToken cancellationToken)
    {
        IManifestFormat? format;
        try
        {
            format = ManifestFormats.For(formats, input.Path);
        }
        catch (InvalidOperationException)
        {
            return Refuse("Two manifest formats claim this file.");
        }
        if (format is null)
            return Refuse($"keyed-merge reads the manifests of its formats only ({string.Join(", ", formats.Select(f => f.Name))}); a lockfile is regenerated, never merged.");
        if (!input.Stages.TryGetValue(1, out var @base))
            return Refuse("Both sides added the file (an add/add conflict): there is no merge base to merge keys against.");

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

        var written = new List<string>();
        var regionNumber = 0;
        foreach (var piece in Enclose(format, document.Pieces))
        {
            switch (piece)
            {
                case Diff3Piece.Common common:
                    written.AddRange(common.Lines);
                    break;
                case Diff3Piece.Region region:
                    regionNumber++;
                    var merge = MergeRegion(format, region, written);
                    if (merge.Refusal is { } refusal)
                        return Refuse($"Conflict region {regionNumber}: {refusal}");
                    written.AddRange(merge.Lines!);
                    break;
            }
        }
        return new ConflictResolution.Content(string.Concat(written));
    }

    /// <summary>Each region widened to whole entries: the lines of the common text around it that
    /// <see cref="IManifestFormat.Enclose"/> names move into all three of its sides.</summary>
    internal static IReadOnlyList<Diff3Piece> Enclose(IManifestFormat format, IReadOnlyList<Diff3Piece> pieces)
    {
        var commons = pieces.Select(p => (p as Diff3Piece.Common)?.Lines.ToList()).ToList();
        var regions = pieces.Select(p => p is Diff3Piece.Region r ? (Ours: r.Ours.ToList(), Base: r.Base.ToList(), Theirs: r.Theirs.ToList()) : default).ToList();
        for (var i = 1; i < pieces.Count - 1; i++)
        {
            if (pieces[i] is not Diff3Piece.Region || commons[i - 1] is not { } before || commons[i + 1] is not { } after)
                continue;
            var (above, below) = format.Enclose(before, after);
            if (above == 0 && below == 0)
                continue;
            if (above < 0 || below < 0 || above > before.Count || below > after.Count)
                throw new InvalidOperationException($"Manifest format '{format.Name}' enclosed lines outside the common text.");
            var head = before.GetRange(before.Count - above, above);
            var tail = after.GetRange(0, below);
            before.RemoveRange(before.Count - above, above);
            after.RemoveRange(0, below);
            foreach (var side in new[] { regions[i].Ours, regions[i].Base, regions[i].Theirs })
            {
                side.InsertRange(0, head);
                side.AddRange(tail);
            }
        }
        return [.. pieces.Select((p, i) => p is Diff3Piece.Region
            ? (Diff3Piece)new Diff3Piece.Region(regions[i].Ours, regions[i].Base, regions[i].Theirs)
            : new Diff3Piece.Common(commons[i]!))];
    }

    internal sealed record RegionMerge(IReadOnlyList<string>? Lines, string? Refusal);

    /// <summary>One region, merged per key. <paramref name="before"/> is the merged text above it.</summary>
    internal static RegionMerge MergeRegion(IManifestFormat format, Diff3Piece.Region region, IReadOnlyList<string> before)
    {
        var ours = format.Read(region.Ours);
        var @base = format.Read(region.Base);
        var theirs = format.Read(region.Theirs);
        if (ours is null || @base is null || theirs is null)
            return new(null, $"a line that is not a keyed entry of {format.Name}.");
        if (Duplicate(ours) || Duplicate(@base) || Duplicate(theirs))
            return new(null, "a side holds one key twice.");

        var baseByKey = @base.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var theirsByKey = theirs.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var oursKeys = ours.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var output = new List<ManifestEntry>();

        foreach (var o in ours.Entries)
        {
            baseByKey.TryGetValue(o.Key, out var b);
            theirsByKey.TryGetValue(o.Key, out var t);
            if (t is null)
            {
                if (b is null)
                    output.Add(o); // the base branch's insertion
                else if (!Same(o, b))
                    return new(null, "an entry removed on one side and changed on the other.");
                continue; // the pull request removed it, the base branch left it alone
            }
            var merged = Merge(format, b, o, t, before);
            if (merged.Refusal is { } refusal)
                return new(null, refusal);
            output.Add(merged.Entry!);
        }
        foreach (var t in theirs.Entries.Where(e => !oursKeys.Contains(e.Key)))
        {
            if (!baseByKey.TryGetValue(t.Key, out var b))
                output.Add(t); // the pull request's insertion, after the base branch's entries
            else if (!Same(t, b))
                return new(null, "an entry removed on one side and changed on the other.");
        }

        var trailing = SameLines(theirs.Trailing, @base.Trailing) || SameLines(theirs.Trailing, ours.Trailing) ? ours.Trailing
            : SameLines(ours.Trailing, @base.Trailing) ? theirs.Trailing
            : null;
        return trailing is null
            ? new(null, "comments or blank lines after the last entry changed on both sides.")
            : new(format.Join(output, trailing, ours, theirs), null);
    }

    private static bool Duplicate(ManifestSection section) => section.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != section.Entries.Count;

    private static bool SameLines(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>The same entry by meaning: its values compared by name (an item's metadata order and spacing do not count),
    /// and the comments that travel with it.</summary>
    private static bool Same(ManifestEntry a, ManifestEntry b)
        => a.Values.Count == b.Values.Count && a.Values.All(v => b.Values.TryGetValue(v.Key, out var other) && other == v.Value)
           && SameLines([.. a.Leading.Select(l => l.Trim())], [.. b.Leading.Select(l => l.Trim())]);

    private sealed record EntryMerge(ManifestEntry? Entry, string? Refusal);

    /// <summary>One key present on both sides. <paramref name="b"/> is null when both inserted it.</summary>
    private static EntryMerge Merge(IManifestFormat format, ManifestEntry? b, ManifestEntry o, ManifestEntry t, IReadOnlyList<string> before)
    {
        if (Same(o, t))
            return new(o, null);
        if (b is not null && Same(b, o))
            return new(t, null);
        if (b is not null && Same(b, t))
            return new(o, null);

        // Both changed: per value name, the same three-way rule; an ordered version both sides raised takes the highest.
        var names = o.Values.Keys.Concat(t.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            string? vb = null;
            b?.Values.TryGetValue(name, out vb);
            o.Values.TryGetValue(name, out var vo);
            t.Values.TryGetValue(name, out var vt);
            if (vo == vt || vt == vb)
                result[name] = vo;
            else if (vo == vb)
                result[name] = vt;
            else if (vo is not null && vt is not null && format.CompareVersions(o, name, vo, vt, before) is { } order)
            {
                // K1: the highest only when both sides raised it; a lowered version is deliberate (a pin away from a
                // broken release) and the highest would undo it silently. Both inserted (no base): nothing to lower.
                if (vb is not null && (format.CompareVersions(o, name, vb, vo, before) is not < 0 || format.CompareVersions(o, name, vb, vt, before) is not < 0))
                    return new(null, "a package version lowered on one side and changed on the other (a pin is deliberate; the highest would undo it).");
                result[name] = order >= 0 ? vo : vt;
            }
            else
                return new(null, "an entry changed on both sides, in a value that has no order (only a package version takes the highest).");
        }
        if (!SameLines(t.Leading, o.Leading) && (b is null || !SameLines(t.Leading, b.Leading)) && (b is null || !SameLines(o.Leading, b.Leading)))
            return new(null, "the comments before an entry changed on both sides.");
        var leading = b is not null && SameLines(o.Leading, b.Leading) ? t.Leading : o.Leading;
        return format.Write(o, result) is { } lines
            ? new(o with { Leading = leading, Lines = lines, Values = result.Where(r => r.Value is not null).ToDictionary(r => r.Key, r => r.Value!, StringComparer.OrdinalIgnoreCase) }, null)
            : new(null, "an entry whose merged values cannot be written back into its text.");
    }

    private static ConflictResolution.Refused Refuse(string detail) => new(EvictionReason.ResolverRefused, detail);
}
