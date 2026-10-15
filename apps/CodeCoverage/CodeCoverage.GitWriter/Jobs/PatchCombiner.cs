using System.Text;
using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>
/// The patches of several regenerate workflows, made into one (merge-queue PRD O36). Each run built the same prepared
/// commit and never saw another's patch, so their changes are independent only when they touch different paths. Each patch
/// is validated on its own (<see cref="PatchValidator"/>, T4), then split into its <c>diff --git</c> sections, kept in the
/// order given. A path two sections change is refused (<see cref="PatchReasons.Overlap"/>) unless the sections are the
/// same text, which is then kept once. The result is a patch <see cref="PatchValidator"/> accepts again.
/// </summary>
public static class PatchCombiner
{
    /// <summary>The combined patch (empty when every run changed nothing), or why it is refused.</summary>
    public static (byte[]? Patch, PatchVerdict Verdict) Combine(IReadOnlyList<byte[]> patches, IReadOnlyList<PathGlob> regeneratedPaths, long maxBytes)
    {
        var total = patches.Sum(p => (long)p.Length);
        if (total > maxBytes)
            return (null, PatchVerdict.Reject(PatchReasons.TooLarge, $"The patches are {total} bytes together; the cap is {maxBytes}."));

        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var patch in patches.Where(p => p.Length > 0))
        {
            var verdict = PatchValidator.Validate(patch, regeneratedPaths, maxBytes);
            if (!verdict.Accepted)
                return (null, verdict);
            foreach (var section in Sections(Encoding.UTF8.GetString(patch)))
            {
                var paths = PatchValidator.Validate(Encoding.UTF8.GetBytes(section), regeneratedPaths, maxBytes).Paths;
                var same = paths.Count > 0 && paths.All(p => owner.TryGetValue(p, out var other) && other == section);
                if (same)
                    continue;
                if (paths.FirstOrDefault(owner.ContainsKey) is { } clash)
                    return (null, PatchVerdict.Reject(PatchReasons.Overlap, "Two regenerate workflows changed this path differently; their patches were made on the same commit, so neither saw the other's change.", clash));
                foreach (var path in paths)
                    owner[path] = section;
                kept.Add(section);
            }
        }
        return (Encoding.UTF8.GetBytes(string.Concat(kept)), PatchVerdict.Accept([.. owner.Keys.Order(StringComparer.Ordinal)]));
    }

    /// <summary>A validated patch's <c>diff --git</c> sections, each ending with a newline.</summary>
    internal static IEnumerable<string> Sections(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var next = text.IndexOf("\ndiff --git ", start, StringComparison.Ordinal);
            var end = next < 0 ? text.Length : next + 1;
            var section = text[start..end];
            yield return section.EndsWith('\n') ? section : section + "\n";
            start = end;
        }
    }
}
