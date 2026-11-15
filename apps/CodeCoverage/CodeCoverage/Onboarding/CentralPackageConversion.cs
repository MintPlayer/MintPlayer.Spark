using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeCoverage.Git;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Onboarding;

/// <summary>Which way a <see cref="CentralPackageConversion"/> goes.</summary>
public enum ECentralPackageDirection
{
    /// <summary>Inline versions → one <c>Directory.Packages.props</c> at the root.</summary>
    ToCentral,

    /// <summary><c>Directory.Packages.props</c> → each <c>PackageReference</c>'s own <c>Version</c>.</summary>
    FromCentral,
}

/// <summary>
/// multi-target-dependency-split-PRD P7 (M7, M9-M11, M15): the conversion of a repository's MSBuild files to or from central
/// package management (CPM), planned from the default branch's tree. A separate custom action opens it as a pull request; the
/// dependency updater never converts on its own (M7).
/// <list type="bullet">
/// <item>M9 ("Highest for all"): to CPM, each package gets one <c>PackageVersion</c>, at the highest version any project
/// referenced; <see cref="Changes"/> lists every reference whose version moves.</item>
/// <item>M10 ("Delete it if only versions"): from CPM, every version is inlined, a <c>VersionOverride</c> winning, and
/// <c>Directory.Packages.props</c> is deleted when nothing but the CPM properties and <c>PackageVersion</c> items remain.</item>
/// <item>M11 ("Conditioned PackageVersion"): per-TFM references become <c>PackageVersion</c> items under
/// <c>'$(TargetFramework)' == 'X'</c>, and the project keeps one versionless reference. From CPM, the reverse.</item>
/// <item>M15: anything the bot cannot tell (a condition other than that one, an <c>Update</c> item, a version in a child
/// element, nested <c>Directory.Packages.props</c> files, a <c>ManagePackageVersionsCentrally</c> that is not literally
/// <c>true</c>) is a <see cref="Refusals"/> entry, and then the repository is left alone: no files.</item>
/// </list>
/// The files are edited as XML with their whitespace kept, so the diff is the references and the items, not a reformat.
/// </summary>
/// <param name="Files">The written and deleted files; empty when there is a refusal.</param>
/// <param name="Changes">M9: each reference whose resolved version moves.</param>
/// <param name="Refusals">Why the repository was not converted.</param>
public sealed record CentralPackageConversion(ECentralPackageDirection Direction, IReadOnlyList<GitWriterFile> Files, IReadOnlyList<string> Changes,
    IReadOnlyList<string> Refusals)
{
    public const string PropsName = "Directory.Packages.props";
    private const string BuildPropsName = "Directory.Build.props";
    private const string Property = "ManagePackageVersionsCentrally";

    /// <summary>The CPM-only properties, removed with <see cref="Property"/> when converting from CPM.</summary>
    private static readonly HashSet<string> CentralProperties = new(StringComparer.Ordinal)
    {
        Property, "CentralPackageTransitivePinningEnabled", "CentralPackageVersionOverrideEnabled", "CentralPackageFloatingVersionsEnabled",
    };

    private static readonly Regex Canonical = new(@"^'\$\(TargetFramework\)' == '([A-Za-z0-9.+-]+)'$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public bool IsEmpty => Files.Count == 0;

    /// <summary>
    /// M15: whether the repository uses CPM: true when a <c>ManagePackageVersionsCentrally</c> is literally <c>true</c>, false
    /// when none is (or each is literally <c>false</c>), null when one is computed and the bot cannot tell.
    /// </summary>
    public static bool? UsesCentral(IReadOnlyDictionary<string, string> files)
    {
        var values = files.Where(f => LinePaths.IsMsBuild(f.Key))
            .SelectMany(f => Regex.Matches(f.Value, $@"<{Property}(?:\s[^>]*)?>([^<]*)</{Property}>", RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Select(m => m.Groups[1].Value.Trim()).ToList();
        if (values.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return true;
        return values.All(v => v.Equals("false", StringComparison.OrdinalIgnoreCase)) ? false : null;
    }

    public static CentralPackageConversion Plan(IReadOnlyDictionary<string, string> files, ECentralPackageDirection direction)
        => direction == ECentralPackageDirection.ToCentral ? ToCentral(files) : FromCentral(files);

    // ═══ To CPM ═════════════════════════════════════════════════════════════════════════════════════

    public static CentralPackageConversion ToCentral(IReadOnlyDictionary<string, string> files)
    {
        var refusals = new List<string>();
        CentralPackageConversion Refused() => new(ECentralPackageDirection.ToCentral, [], [], refusals);

        if (files.Keys.FirstOrDefault(IsCentralProps) is { } existing)
        {
            refusals.Add($"`{existing}` exists already; the bot converts only a repository without one.");
            return Refused();
        }
        if (UsesCentral(files) is not false)
        {
            refusals.Add($"`{Property}` is set somewhere, so the repository is not plainly without central package management.");
            return Refused();
        }
        var documents = Parse(files, refusals);
        if (refusals.Count > 0)
            return Refused();

        // Every reference with the TFMs it builds for (one, under a canonical ItemGroup; else the file's list).
        var references = new List<Reference>();
        foreach (var document in documents)
        {
            foreach (var element in PackageItems(document.Xml, "PackageReference"))
            {
                if (Describe(document, element, refusals) is not { } reference)
                    continue;
                if (element.Attribute("VersionOverride") is not null)
                {
                    refusals.Add($"`{reference.Id}` in `{document.Path}` has a `VersionOverride`, which only central package management reads.");
                    continue;
                }
                if (element.Attribute("Version")?.Value is not { Length: > 0 } version)
                {
                    refusals.Add(element.Elements().Any(e => e.Name.LocalName == "Version")
                            ? $"`{reference.Id}` in `{document.Path}` names its version in a child element; write it as a `Version` attribute first."
                            : $"`{reference.Id}` in `{document.Path}` has no version.");
                    continue;
                }
                references.Add(reference with { Version = version, Tfms = reference.Tfm is { } tfm ? [tfm] : TfmsOf(files, document.Path) });
            }
        }
        if (refusals.Count > 0)
            return Refused();

        // Per package: one version, or (M11) one per TFM where per-TFM references disagree.
        var central = new List<(string Id, string? Tfm, string Version)>();
        var changes = new List<string>();
        foreach (var package in references.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var id = package.First().Id;
            if (Highest(package.Select(r => r.Version!)) is not { } highest)
            {
                refusals.Add($"`{id}` has versions the bot cannot compare ({string.Join(", ", package.Select(r => r.Version).Distinct())}); give it one version first.");
                continue;
            }
            var split = package.Any(r => r.Tfm is not null);
            var perTfm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (split)
            {
                if (package.FirstOrDefault(r => r.Tfm is null && r.Tfms!.Count == 0) is { } unknown)
                {
                    refusals.Add($"`{id}` is referenced per TFM elsewhere, and the bot cannot tell which TFMs `{unknown.Path}` builds for.");
                    continue;
                }
                foreach (var tfm in package.SelectMany(r => r.Tfms!).Distinct(StringComparer.OrdinalIgnoreCase))
                    perTfm[tfm] = Highest(package.Where(r => r.Tfms!.Contains(tfm, StringComparer.OrdinalIgnoreCase)).Select(r => r.Version!))!;
                split = perTfm.Values.Distinct(StringComparer.Ordinal).Count() > 1;
            }
            if (split)
                central.AddRange(perTfm.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => (id, (string?)p.Key, p.Value)));
            else
                central.Add((id, null, highest));
            foreach (var reference in package)
            {
                var target = split ? Highest(reference.Tfms!.Select(t => perTfm[t]))! : highest;
                if (!string.Equals(reference.Version, target, StringComparison.Ordinal))
                    changes.Add($"`{id}` in `{reference.Path}`{(reference.Tfm is null ? "" : $" ({reference.Tfm})")}: {reference.Version} → {target}");
            }
        }
        if (refusals.Count > 0)
            return Refused();

        // The project files: versions removed; per-TFM references of one package collapse into one versionless reference
        // where they cover the project's every TFM.
        foreach (var reference in references)
            reference.Element.Attribute("Version")!.Remove();
        foreach (var group in references.Where(r => r.Tfm is not null).GroupBy(r => (r.Path, Id: r.Id.ToLowerInvariant())))
        {
            var first = group.First();
            var shapes = group.Select(r => r.Element.ToString(SaveOptions.DisableFormatting)).Distinct(StringComparer.Ordinal).Count();
            var covered = group.Select(r => r.Tfm!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tfms = TfmsOf(files, first.Path);
            if (shapes != 1 || tfms.Count == 0 || !covered.SetEquals(tfms) || group.Count() != covered.Count)
                continue;
            var holder = first.Element.Parent!;
            if (holder.Elements().Count() == 1)
                holder.Attribute("Condition")!.Remove();
            else
            {
                var moved = new XElement(first.Element);
                Remove(first.Element);
                InsertGroupAfter(holder, null, moved);
            }
            foreach (var other in group.Skip(1))
                Remove(other.Element);
        }

        var written = documents.Where(d => references.Any(r => r.Path == d.Path)).Select(d => new GitWriterFile(d.Path, d.Render())).ToList();
        var newline = documents.Count > 0 && documents.Count(d => d.Crlf) * 2 > documents.Count ? "\r\n" : "\n";
        written.Add(new GitWriterFile(PropsName, RenderProps(central).Replace("\n", newline, StringComparison.Ordinal)));
        return new CentralPackageConversion(ECentralPackageDirection.ToCentral, [.. written.OrderBy(f => f.Path, StringComparer.Ordinal)], changes, []);
    }

    private static string RenderProps(List<(string Id, string? Tfm, string Version)> central)
    {
        var text = new System.Text.StringBuilder("<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n");
        foreach (var group in central.GroupBy(c => c.Tfm).OrderBy(g => g.Key is null ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(group.Key is null ? "  <ItemGroup>\n" : $"  <ItemGroup Condition=\"'$(TargetFramework)' == '{group.Key}'\">\n");
            foreach (var item in group)
                text.Append($"    {new XElement("PackageVersion", new XAttribute("Include", item.Id), new XAttribute("Version", item.Version)).ToString(SaveOptions.DisableFormatting)}\n");
            text.Append("  </ItemGroup>\n");
        }
        return text.Append("</Project>\n").ToString();
    }

    // ═══ From CPM ═══════════════════════════════════════════════════════════════════════════════════

    public static CentralPackageConversion FromCentral(IReadOnlyDictionary<string, string> files)
    {
        var refusals = new List<string>();
        CentralPackageConversion Refused() => new(ECentralPackageDirection.FromCentral, [], [], refusals);

        var props = files.Keys.Where(IsCentralProps).ToList();
        if (props.Count != 1)
        {
            refusals.Add(props.Count == 0
                ? $"There is no `{PropsName}`, so the repository does not use central package management."
                : $"There are {props.Count} `{PropsName}` files ({string.Join(", ", props.Select(p => $"`{p}`"))}); the bot converts only one.");
            return Refused();
        }
        var propsPath = props[0];
        if (UsesCentral(new Dictionary<string, string> { [propsPath] = files[propsPath] }) is not true)
        {
            refusals.Add($"`{Property}` is not literally `true` in `{propsPath}`, so the bot cannot tell that it is on.");
            return Refused();
        }
        var folder = DirectoryOf(propsPath);
        var documents = Parse(files.Where(f => folder.Length == 0 || f.Key.StartsWith(folder + "/", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal), refusals);
        if (refusals.Count > 0)
            return Refused();
        var central = documents.Single(d => d.Path == propsPath);

        // The central versions: per package, an unconditioned one and per-TFM ones.
        var versions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var items = PackageItems(central.Xml, "PackageVersion").ToList();
        foreach (var element in items)
        {
            if (Describe(central, element, refusals) is not { } item)
                continue;
            if (element.Attribute("Version")?.Value is not { Length: > 0 } version)
            {
                refusals.Add($"`{item.Id}` in `{propsPath}` has no `Version` attribute.");
                continue;
            }
            (versions.TryGetValue(item.Id, out var map) ? map : versions[item.Id] = new(StringComparer.OrdinalIgnoreCase))[item.Tfm ?? ""] = version;
        }
        if (central.Xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "GlobalPackageReference") is { } global)
            refusals.Add($"`{propsPath}` has a `GlobalPackageReference` (`{global.Attribute("Include")?.Value}`), which has no form without central package management.");
        if (refusals.Count > 0)
            return Refused();

        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents.Where(d => d.Path != propsPath))
        {
            if (Regex.IsMatch(files[document.Path], $@"<{Property}(?:\s[^>]*)?>\s*false\s*</{Property}>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                continue;   // the project opted out: its references carry their versions already
            foreach (var element in PackageItems(document.Xml, "PackageReference").ToList())
            {
                if (Describe(document, element, refusals) is not { } reference)
                    continue;
                if (element.Attribute("Version") is not null)
                    continue;
                if (element.Attribute("VersionOverride") is { } overriding)
                {
                    SetVersion(element, overriding.Value);
                    touched.Add(document.Path);
                    continue;
                }
                if (!versions.TryGetValue(reference.Id, out var map))
                {
                    refusals.Add($"`{reference.Id}` in `{document.Path}` has no `PackageVersion` in `{propsPath}`.");
                    continue;
                }
                var tfms = reference.Tfm is { } own ? [own] : TfmsOf(files, document.Path);
                var perTfm = tfms.Select(t => (Tfm: t, Version: map.GetValueOrDefault(t) ?? map.GetValueOrDefault(""))).ToList();
                if (perTfm.Count == 0 && map.Count == 1 && map.ContainsKey(""))
                    perTfm = [("", map[""])];
                if (perTfm.Count == 0)
                {
                    refusals.Add($"`{reference.Id}` in `{document.Path}` has only per-TFM `PackageVersion` items, and the bot cannot tell which TFMs the file builds for.");
                    continue;
                }
                if (perTfm.Where(p => p.Version is null).Select(p => p.Tfm).FirstOrDefault() is { } absent)
                {
                    refusals.Add($"`{reference.Id}` in `{document.Path}` has no `PackageVersion` for {absent}.");
                    continue;
                }
                touched.Add(document.Path);
                if (perTfm.Select(p => p.Version).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    SetVersion(element, perTfm[0].Version!);
                    continue;
                }
                // M11 reversed: one reference per TFM, each under its canonical ItemGroup.
                var holder = element.Parent!;
                var copies = perTfm.Select(p =>
                {
                    var copy = new XElement(element);
                    SetVersion(copy, p.Version!);
                    return (p.Tfm, copy);
                }).ToList();
                if (holder.Elements().Count() == 1)
                {
                    SetVersion(element, copies[0].copy.Attribute("Version")!.Value);
                    holder.SetAttributeValue("Condition", $"'$(TargetFramework)' == '{copies[0].Tfm}'");
                    var after = holder;
                    foreach (var (tfm, copy) in copies.Skip(1))
                        after = InsertGroupAfter(after, tfm, copy);
                }
                else
                {
                    Remove(element);
                    var after = holder;
                    foreach (var (tfm, copy) in copies)
                        after = InsertGroupAfter(after, tfm, copy);
                }
            }
        }
        if (refusals.Count > 0)
            return Refused();

        // M10: the central file loses its versions and CPM properties; deleted when nothing else is left.
        foreach (var element in items)
            Remove(element);
        foreach (var property in central.Xml.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup" && CentralProperties.Contains(e.Name.LocalName)).ToList())
            Remove(property);
        foreach (var group in central.Xml.Descendants().Where(e => e.Name.LocalName is "ItemGroup" or "PropertyGroup" && !e.Elements().Any()).ToList())
            Remove(group);
        var written = documents.Where(d => touched.Contains(d.Path)).Select(d => new GitWriterFile(d.Path, d.Render())).ToList();
        written.Add(central.Xml.Root!.Elements().Any() ? new GitWriterFile(propsPath, central.Render()) : new GitWriterFile(propsPath, null, Delete: true));
        return new CentralPackageConversion(ECentralPackageDirection.FromCentral, [.. written.OrderBy(f => f.Path, StringComparer.Ordinal)], [], []);
    }

    // ═══ The pull request ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The pull request's head; the git writer names it the same way (<c>DependencyBranches.CentralPackageManagement</c>).</summary>
    public const string Branch = "mintplayer/central-package-management";

    public const string ToTitle = "Use central package management";
    public const string FromTitle = "Stop using central package management";

    public string Title => Direction == ECentralPackageDirection.ToCentral ? ToTitle : FromTitle;

    public string Body()
    {
        var body = new System.Text.StringBuilder();
        if (Direction == ECentralPackageDirection.ToCentral)
        {
            body.Append($"This moves every package version into one `{PropsName}` at the root and turns on `{Property}`, as asked with **Convert to central package management**.\n\n");
            body.Append(Changes.Count == 0
                ? "Every project already used the same version of each package, so no resolved version changes.\n"
                : "Each package now has one version, the highest any project used, so these references move:\n\n");
            foreach (var change in Changes)
                body.Append($"- {change}\n");
        }
        else
        {
            body.Append($"This writes each package's version back into its `PackageReference` (a `VersionOverride` wins) and turns off central package management, as asked with **Convert from central package management**.\n\n");
            body.Append(Files.Any(f => f.Delete)
                ? $"`{PropsName}` held only versions, so it is deleted.\n"
                : $"`{PropsName}` keeps what is not about package versions.\n");
        }
        body.Append("\nPer-TFM versions stay per TFM, under `'$(TargetFramework)' == 'X'`.\n");
        return body.ToString();
    }

    // ═══ XML ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One parsed MSBuild file; <see cref="Render"/> writes it back with its own prolog, ending and line breaks.</summary>
    private sealed record Document(string Path, XDocument Xml, string Prefix, string Suffix, bool Crlf)
    {
        public string Render()
        {
            var text = Prefix + Xml.Root!.ToString(SaveOptions.DisableFormatting) + Suffix;
            text = text.ReplaceLineEndings("\n");
            return Crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
        }
    }

    /// <summary>One <c>PackageReference</c> or <c>PackageVersion</c>: its package, its canonical TFM (null when unconditioned).</summary>
    private sealed record Reference(string Path, XElement Element, string Id, string? Tfm)
    {
        public string? Version { get; init; }
        public IReadOnlyList<string>? Tfms { get; init; }
    }

    private static List<Document> Parse(IReadOnlyDictionary<string, string> files, List<string> refusals)
    {
        var documents = new List<Document>();
        foreach (var (path, text) in files.Where(f => LinePaths.IsMsBuild(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            try
            {
                var xml = XDocument.Parse(text.StartsWith('﻿') ? text[1..] : text, LoadOptions.PreserveWhitespace);
                var name = xml.Root!.Name.LocalName;
                var start = Regex.Match(text, $@"<{Regex.Escape(name)}[\s>/]", RegexOptions.None, TimeSpan.FromSeconds(1)).Index;
                var end = text.TrimEnd().Length;
                documents.Add(new Document(path, xml, text[..start], text[end..], text.Contains("\r\n", StringComparison.Ordinal)));
            }
            catch (XmlException e)
            {
                refusals.Add($"`{path}` is not XML the bot can read: {e.Message}");
            }
        }
        return documents;
    }

    private static IEnumerable<XElement> PackageItems(XDocument document, string name)
        => document.Descendants().Where(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "ItemGroup");

    /// <summary>The package and TFM of an item, or null after a refusal (M15: an Update item, a condition other than the
    /// canonical ItemGroup one).</summary>
    private static Reference? Describe(Document document, XElement element, List<string> refusals)
    {
        if (element.Attribute("Include")?.Value is not { Length: > 0 } id)
        {
            refusals.Add($"`{document.Path}` has a `{element.Name.LocalName}` without `Include` (an `Update` item); the bot cannot tell which projects it changes.");
            return null;
        }
        if (element.Attribute("Condition") is { } own)
        {
            refusals.Add($"`{id}` in `{document.Path}` has a condition of its own (`{own.Value}`); the bot moves only `'$(TargetFramework)' == 'X'` on its ItemGroup.");
            return null;
        }
        if (element.Parent?.Attribute("Condition")?.Value is not { } condition)
            return new Reference(document.Path, element, id, null);
        if (Canonical.Match(condition) is { Success: true } match)
            return new Reference(document.Path, element, id, match.Groups[1].Value);
        refusals.Add($"`{id}` in `{document.Path}` is under `{condition}`; the bot moves only `'$(TargetFramework)' == 'X'`.");
        return null;
    }

    /// <summary>A project's TFMs: its own literal <c>TargetFramework(s)</c>, else the nearest Directory.Build.props's; empty
    /// when computed or unknown.</summary>
    private static List<string> TfmsOf(IReadOnlyDictionary<string, string> files, string path)
    {
        static List<string>? Literal(string text)
        {
            var match = Regex.Match(text, @"<TargetFrameworks?>([^<]*)</TargetFrameworks?>", RegexOptions.None, TimeSpan.FromSeconds(1));
            return match.Success && !match.Groups[1].Value.Contains('$', StringComparison.Ordinal)
                ? [.. match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : null;
        }
        if (files.TryGetValue(path, out var own) && !path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            && Literal(own) is { } list)
            return list;
        for (var dir = DirectoryOf(path); ; dir = DirectoryOf(dir))
        {
            if (files.TryGetValue(dir.Length == 0 ? BuildPropsName : $"{dir}/{BuildPropsName}", out var props) && Literal(props) is { } inherited)
                return inherited;
            if (dir.Length == 0)
                return [];
        }
    }

    /// <summary>The highest of <paramref name="versions"/>; the one version when they agree; null when they disagree and
    /// one is no SemVer (a range, a property, a four-part version).</summary>
    private static string? Highest(IEnumerable<string> versions)
    {
        var distinct = versions.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1)
            return distinct[0];
        var parsed = distinct.Select(v => SemVer.TryParse(v, out var s) ? (v, s) : (v, null!)).ToList();
        return parsed.Any(p => p.s is null) ? null : parsed.MaxBy(p => p.s).v;
    }

    /// <summary>Sets <c>Version</c> right after <c>Include</c>, dropping a <c>VersionOverride</c>.</summary>
    private static void SetVersion(XElement element, string version)
    {
        var attributes = element.Attributes().Where(a => a.Name.LocalName is not ("Version" or "VersionOverride")).ToList();
        element.RemoveAttributes();
        foreach (var attribute in attributes)
        {
            element.Add(attribute);
            if (attribute.Name.LocalName == "Include")
                element.Add(new XAttribute("Version", version));
        }
    }

    /// <summary>Removes an element with the whitespace before it, so no blank line is left.</summary>
    private static void Remove(XElement element)
    {
        var parent = element.Parent;
        if (element.PreviousNode is XText { } text && string.IsNullOrWhiteSpace(text.Value))
            text.Remove();
        element.Remove();
        if (parent is { Name.LocalName: "ItemGroup" } && !parent.Elements().Any())
            Remove(parent);
    }

    /// <summary>A new ItemGroup (canonical for <paramref name="tfm"/>, plain for null) holding <paramref name="item"/>, after
    /// <paramref name="after"/>, laid out like it.</summary>
    private static XElement InsertGroupAfter(XElement after, string? tfm, XElement item)
    {
        var separator = after.PreviousNode is XText { } text && string.IsNullOrWhiteSpace(text.Value) ? text.Value : "\n  ";
        var indent = separator[(separator.LastIndexOf('\n') + 1)..];
        var inner = after.Elements().FirstOrDefault()?.PreviousNode is XText { } first && string.IsNullOrWhiteSpace(first.Value) ? first.Value : $"\n{indent}  ";
        var group = new XElement(after.Name.Namespace + "ItemGroup", new XText(inner), item, new XText("\n" + indent));
        if (tfm is not null)
            group.SetAttributeValue("Condition", $"'$(TargetFramework)' == '{tfm}'");
        after.AddAfterSelf(new XText(separator), group);
        return group;
    }

    private static bool IsCentralProps(string path) => string.Equals(System.IO.Path.GetFileName(path), PropsName, StringComparison.OrdinalIgnoreCase);

    private static string DirectoryOf(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : string.Empty;
}
