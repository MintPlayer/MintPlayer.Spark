namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// Gap A's substitution check (docs/code-coverage/dependency-updates-PRD.md §13.11.4): a changed manifest line may differ
/// from the line it replaces only by planned <c>current → new</c> token swaps (the value, and the digest where the record
/// pins one), each at a token boundary, on a line that names the planned dependency. S13 showed every apply adapter's
/// edit is exactly that shape (all 16 cases "pure"); a native command that re-sorts or reformats fails it on purpose.
/// </summary>
public static class SubstitutionCheck
{
    /// <summary>How many swaps one line may hold (a Docker <c>FROM</c> swaps its tag and its digest).</summary>
    public const int MaxSwapsPerLine = 4;

    /// <summary>How many old-side lines above a changed line may name the dependency (a Maven <c>&lt;version&gt;</c> under
    /// its <c>&lt;artifactId&gt;</c>, a NuGet child <c>&lt;Version&gt;</c>, a dotnet-tools <c>"version"</c> under the tool).</summary>
    public const int AnchorLinesAbove = 3;

    private sealed record Swap(string From, string To, DependencyPlanEntry Entry);

    /// <summary>The entries whose swaps turn <paramref name="old"/> into <paramref name="new"/>, or null when no planned
    /// swap sequence does, or when an entry's line does not name it.</summary>
    public static IReadOnlyList<DependencyPlanEntry>? Explain(string old, string @new, IReadOnlyList<string> linesAbove, IReadOnlyList<DependencyPlanEntry> entries)
    {
        if (old == @new || old.Length > 8192 || @new.Length > 8192)
            return null;
        var swaps = new List<Swap>();
        foreach (var entry in entries.Where(e => !e.IsLockfileOnly))
        {
            if (entry.CurrentValue.Length > 0 && entry.CurrentValue != entry.NewValue)
                swaps.Add(new Swap(entry.CurrentValue, entry.NewValue, entry));
            if (entry.CurrentDigest is { Length: > 0 } from && entry.NewDigest is { Length: > 0 } to && from != to)
                swaps.Add(new Swap(from, to, entry));
        }
        var used = new List<Swap>();
        if (!Search(old, @new, 0, MaxSwapsPerLine, swaps, used))
            return null;
        // Each swap the line used must belong to an entry the line (or the lines just above it) names; every such entry
        // is explained, so two records sharing one Maven property or catalog version both count as applied.
        var context = new List<string>(linesAbove.TakeLast(AnchorLinesAbove)) { old, @new };
        var explained = new List<DependencyPlanEntry>();
        foreach (var swap in used)
        {
            var named = swaps.Where(s => s.From == swap.From && s.To == swap.To)
                .Select(s => s.Entry)
                .Where(e => Anchors(e).Any(a => context.Any(l => l.Contains(a, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            if (named.Count == 0)
                return null;
            explained.AddRange(named);
        }
        return explained.Distinct().ToList();
    }

    /// <summary>Left-to-right, non-overlapping swaps; a swap is tried only at or before the first difference, so the
    /// search stays linear in practice.</summary>
    private static bool Search(string current, string target, int from, int depth, List<Swap> swaps, List<Swap> used)
    {
        if (current == target)
            return true;
        if (depth == 0)
            return false;
        var mismatch = FirstMismatch(current, target);
        foreach (var swap in swaps)
        {
            for (var i = current.IndexOf(swap.From, from, StringComparison.Ordinal); i >= 0 && i <= mismatch; i = current.IndexOf(swap.From, i + 1, StringComparison.Ordinal))
            {
                if (!AtBoundary(current, i, swap.From.Length))
                    continue;
                var next = string.Concat(current.AsSpan(0, i), swap.To, current.AsSpan(i + swap.From.Length));
                var end = i + swap.To.Length;
                if (end > target.Length || string.CompareOrdinal(next, 0, target, 0, end) != 0)
                    continue;
                used.Add(swap);
                if (Search(next, target, end, depth - 1, swaps, used))
                    return true;
                used.RemoveAt(used.Count - 1);
            }
        }
        return false;
    }

    private static int FirstMismatch(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
                return i;
        }
        return length;
    }

    private static bool IsVersionChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-';

    /// <summary>A token that starts (ends) with a letter or digit must not continue a longer version on that side, so
    /// <c>1.0.1 → 1.0.2</c> never rewrites <c>1.0.15</c> or <c>1.0.1-beta</c>.</summary>
    private static bool AtBoundary(string text, int index, int length)
    {
        var first = text[index];
        var last = text[index + length - 1];
        if (char.IsAsciiLetterOrDigit(first) && index > 0 && IsVersionChar(text[index - 1]))
            return false;
        if (char.IsAsciiLetterOrDigit(last) && index + length < text.Length && IsVersionChar(text[index + length]))
            return false;
        return true;
    }

    /// <summary>The names a line holding the entry's value would carry: the dependency and package names, their last
    /// segment (a Maven artifactId, an image's repository), the shared variable, and the key a platform file uses.</summary>
    public static IEnumerable<string> Anchors(DependencyPlanEntry entry)
    {
        foreach (var name in new[] { entry.DepName, entry.PackageName }.OfType<string>().Where(n => n.Length > 0))
        {
            yield return name;
            var last = name[(name.LastIndexOfAny(['/', ':']) + 1)..];
            if (last.Length >= 2 && last != name)
                yield return last;
        }
        if (entry.SharedVariableName is { Length: > 0 } shared)
            yield return shared;
        switch (entry.Datasource)
        {
            case "dotnet-version":
                yield return "\"version\"";
                break;
            case "golang-version":
                yield return "go ";
                yield return "toolchain";
                break;
        }
    }
}
