using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// multi-target-dependency-split-PRD P5 (D-e, S4): an MSBuild file in which a plan item with a layout moves a package into one
/// canonical <c>ItemGroup</c> per TFM. That edit adds and removes lines, so the line rule cannot admit it; the file is checked
/// as XML instead, "same XML, regrouped":
/// <list type="number">
/// <item>Every occurrence of a layout package after the patch is the element the file had (attributes and children), with
/// only its version changed, with no <c>Condition</c> of its own, in an <c>ItemGroup</c> whose condition is exactly
/// <c>'$(TargetFramework)' == 'X'</c>; one per planned TFM, at the planned version, and no other.</item>
/// <item>A non-layout entry of the same file may swap its current value for its new one on an element of its package.</item>
/// <item>A <c>TargetFrameworks</c> value may become the one the plan's platform move names for this file (P3).</item>
/// <item>With those taken out, and empty <c>ItemGroup</c>s dropped, the file before and after is the same XML.</item>
/// </list>
/// Nothing is guessed: anything else is <see cref="DependencyPatchReasons.UnplannedChange"/>.
/// </summary>
internal static class DependencyLayoutCheck
{
    private static readonly Regex Canonical = new(@"^'\$\(TargetFramework\)' == '([A-Za-z0-9.+-]+)'$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ItemNames = new(StringComparer.Ordinal) { "PackageReference", "PackageVersion" };

    public static async Task<DependencyPatchVerdict?> ValidateAsync(PatchFile file, List<DependencyPlanEntry> entries,
        List<(DependencyPlatformMove Move, DependencyPlatformLocation Location)> platform, IBaseTree baseTree,
        HashSet<DependencyPlanEntry> used, HashSet<string> platformApplied, CancellationToken cancellationToken)
    {
        DependencyPatchVerdict Refuse(string detail) => DependencyPatchVerdict.Reject(DependencyPatchReasons.UnplannedChange, detail, file.Path);

        var original = await baseTree.ReadTextAsync(file.Path, cancellationToken);
        var patched = original is null ? null : file.ApplyTo(original);
        if (original is null || patched is null)
            return Refuse("The file at the planned commit is not readable, or the patch does not apply to it.");
        XDocument before, after;
        try
        {
            before = Parse(original);
            after = Parse(patched);
        }
        catch (XmlException e)
        {
            return Refuse($"A file with a planned per-TFM layout must be XML before and after the patch: {e.Message}");
        }

        var applied = new List<DependencyPlanEntry>();
        foreach (var entry in entries.Where(e => e.Layout is { Count: > 0 }))
        {
            var old = Items(before, entry.DepName);
            var now = Items(after, entry.DepName);
            if (old.Count == 0)
                return Refuse($"{entry.DepName} is not in the file at the planned commit.");
            var template = Shape(old.FirstOrDefault(e => e.Parent?.Attribute("Condition") is null) ?? old[0]);
            var planned = entry.Layout!.ToDictionary(l => l.TargetFramework, l => l.Version, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in now)
            {
                if (element.Attribute("Condition") is not null)
                    return Refuse($"{entry.DepName} has a Condition of its own after the patch; the bot writes the ItemGroup's.");
                var condition = element.Parent?.Attribute("Condition")?.Value;
                if (condition is null || Canonical.Match(condition) is not { Success: true } match)
                    return Refuse($"{entry.DepName} is under {(condition is null ? "no condition" : $"'{condition}'")}; a planned layout writes only '$(TargetFramework)' == 'X'.");
                var tfm = match.Groups[1].Value;
                if (!planned.TryGetValue(tfm, out var version) || !seen.Add(tfm))
                    return Refuse($"{entry.DepName} for {tfm} is not in the planned layout, or appears twice.");
                if (VersionOf(element) != version)
                    return Refuse($"{entry.DepName} for {tfm} is {VersionOf(element)}; the plan says {version}.");
                if (!XNode.DeepEquals(Shape(element), template))
                    return Refuse($"{entry.DepName} for {tfm} is not the element the file had with only its version changed.");
            }
            if (planned.Keys.FirstOrDefault(t => !seen.Contains(t)) is { } absent)
                return Refuse($"{entry.DepName} has no ItemGroup for {absent}, which the plan names.");
            foreach (var element in old.Concat(now))
                element.Remove();
            applied.Add(entry);
        }

        // An ordinary entry of the same file: its current value swapped for its new one, nothing else.
        foreach (var entry in entries.Where(e => e.Layout is not { Count: > 0 } && e.CurrentValue != e.NewValue))
        {
            var swapped = false;
            foreach (var element in Items(after, entry.DepName).Where(e => VersionOf(e) == entry.NewValue))
            {
                SetVersion(element, entry.CurrentValue);
                swapped = true;
            }
            if (swapped)
                applied.Add(entry);
        }

        // P3: the TFM list the platform move names for this file.
        foreach (var (move, _) in platform.DistinctBy(p => p.Move.Group))
        {
            if (move.Move.TargetFrameworks?.FirstOrDefault(t => t.File == file.Path) is not { } list)
                continue;
            var oldLists = Elements(before, "TargetFrameworks");
            var newLists = Elements(after, "TargetFrameworks");
            if (oldLists.Count != newLists.Count)
                continue;
            for (var i = 0; i < newLists.Count; i++)
            {
                if (newLists[i].Value == list.Value && oldLists[i].Value != list.Value)
                {
                    newLists[i].Value = oldLists[i].Value;
                    platformApplied.Add(move.Group);
                }
            }
        }

        DropEmptyItemGroups(before);
        DropEmptyItemGroups(after);
        if (!XNode.DeepEquals(before, after))
            return Refuse("Beyond the planned per-TFM layout, the file changed (an element, attribute, condition or comment the plan does not name).");
        used.UnionWith(applied);
        return null;
    }

    private static XDocument Parse(string text)
        => XDocument.Parse(text.StartsWith('﻿') ? text[1..] : text, LoadOptions.None);

    private static List<XElement> Elements(XDocument document, string name)
        => [.. document.Descendants().Where(e => e.Name.LocalName == name)];

    private static List<XElement> Items(XDocument document, string depName)
        => [.. document.Descendants().Where(e => ItemNames.Contains(e.Name.LocalName)
            && string.Equals(e.Attribute("Include")?.Value, depName, StringComparison.OrdinalIgnoreCase))];

    private static string? VersionOf(XElement element)
        => element.Attribute("Version")?.Value ?? element.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value;

    private static void SetVersion(XElement element, string version)
    {
        if (element.Attribute("Version") is { } attribute)
            attribute.Value = version;
        else if (element.Elements().FirstOrDefault(c => c.Name.LocalName == "Version") is { } child)
            child.Value = version;
    }

    /// <summary>The element without its version: what must be equal before and after.</summary>
    private static XElement Shape(XElement element)
    {
        var copy = new XElement(element);
        copy.Attribute("Version")?.Remove();
        copy.Elements().Where(c => c.Name.LocalName == "Version").Remove();
        return copy;
    }

    private static void DropEmptyItemGroups(XDocument document)
        => document.Descendants().Where(e => e.Name.LocalName == "ItemGroup" && !e.Nodes().Any(n => n is XElement or XComment)).ToList().ForEach(e => e.Remove());
}
