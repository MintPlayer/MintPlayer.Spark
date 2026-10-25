using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Feedback;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.PackageVersioning;

/// <summary>A version tag the app pushes when the onboarding pull request merges (D17): the version the package had.</summary>
public sealed record VersioningSeed(string PackageId, PackageKind Kind, string Version);

/// <summary>What the versioning onboarding pull request writes (D17), the seeds it records, and its body.</summary>
/// <param name="Notes">What the planner could not do itself, for the person merging the pull request.</param>
public sealed record VersioningOnboardingPlan(IReadOnlyList<GitWriterFile> Files, IReadOnlyList<VersioningSeed> Seeds, IReadOnlyList<string> Notes, string Body);

/// <summary>
/// D17: the pull request that switches a repository to tag-driven versions. It references <c>MintPlayer.Versioning.MSBuild</c>
/// once, replaces every hand-written version with <c>0.0.0-PLACEHOLDER</c> (recording it as the package's seed), puts the
/// action step in front of every push-triggered job's first pack or publish step, and adds the <c>versioning:</c> section.
/// Every edit is textual, so the diff holds the edit and nothing else; what it cannot place safely goes into the body.
/// </summary>
public static partial class VersioningOnboarding
{
    public const string Title = "Set up MintPlayer package versioning";
    public const string Branch = "mintplayer/versioning-onboarding";
    public const string ActionRepository = "MintPlayer/MintPlayer.Spark";
    public const string ActionPath = "MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action";
    public const string ActionTag = "versioning-v1";
    public const string PackageId = "MintPlayer.Versioning.MSBuild";
    /// <summary>The version referenced when nuget.org's <see cref="PackageIndexUrl"/> cannot be read.</summary>
    public const string PackageVersion = "1.0.1";
    public const string PackageIndexUrl = "https://api.nuget.org/v3-flatcontainer/mintplayer.versioning.msbuild/index.json";
    public const string Placeholder = "0.0.0-PLACEHOLDER";
    public const string StepName = "Version the packages";
    /// <summary>The name setup-dotnet looks for in the repository root, case-insensitively.</summary>
    private const string NuGetConfigName = "nuget.config";
    private const string GitHubFeedHost = "nuget.pkg.github.com";
    private const string NuGetOrgSource = "https://api.nuget.org/v3/index.json";

    private const long MaxFileBytes = Limits.MaxManifestBytes;
    private const int MaxMSBuildFiles = 500;

    private static readonly string[] VersionProperties = ["Version", "PackageVersion", "VersionPrefix"];

    /// <summary>The section appended to <c>coverage.yml</c>: present, so the repository is versioned (D17); the rest commented defaults.</summary>
    public static readonly string Section = """

        # MintPlayer package versioning (added by the onboarding pull request). The publish workflow tags every release
        # `<package id>/v<version>`; the commented lines are the defaults.
        versioning:
          mode: server                       # server | local: local runs the engine on the runner, without the app
        #   releaseBranches: ["release/*.x"]
        #   ignore: ["**/*.md"]              # changed files that never release a package
        #   prerelease: upstream             # alpha | beta | next | preview | rc pins the label; upstream mirrors dependencies and the SDK
        #   groups:                         # packages that always share one version
        #     - name: core
        #       packages: [Acme.Core, Acme.Core.*]
        #   packages:
        #     Acme.Generators: { anchor: free }   # free, or the framework package whose major it follows

        """.ReplaceLineEndings("\n");

    /// <param name="tree">The default branch's head.</param>
    /// <param name="taggedIds">Packages that already have a version tag: they keep it and get no seed.</param>
    /// <param name="actionSha">The commit <see cref="ActionTag"/> resolves to, pinned in the step; null keeps the tag.</param>
    /// <param name="packageVersion">The <see cref="PackageId"/> version to reference (<see cref="LatestStable"/>); null takes <see cref="PackageVersion"/>.</param>
    /// <param name="owner">The repository's owner, which <c>${{ github.repository_owner }}</c> in a workflow resolves to.</param>
    public static Task<VersioningOnboardingPlan> PlanAsync(IRepositoryTree tree, IReadOnlySet<string> taggedIds, string? actionSha,
        string? packageVersion = null, string? owner = null, CancellationToken cancellationToken = default)
        => PlanAsync(tree, (id, _) => Task.FromResult(taggedIds.Contains(id)), actionSha, packageVersion, owner, cancellationToken);

    /// <param name="isTagged">Whether a package already has a version tag, asked once per package the graph finds.</param>
    public static async Task<VersioningOnboardingPlan> PlanAsync(IRepositoryTree tree, Func<string, CancellationToken, Task<bool>> isTagged, string? actionSha,
        string? packageVersion = null, string? owner = null, CancellationToken cancellationToken = default)
    {
        var files = new Files(tree, cancellationToken);
        var notes = new List<string>();
        var seeds = new List<VersioningSeed>();

        var graph = await ProjectGraphBuilder.BuildAsync(tree, cancellationToken: cancellationToken);
        notes.AddRange(graph.Warnings);
        var packages = graph.Packages.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var taggedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
            if (await isTagged(package.Id!, cancellationToken)) taggedIds.Add(package.Id!);

        foreach (var package in packages)
            if (!taggedIds.Contains(package.Id!) && Seed(package, notes) is { } seed) seeds.Add(seed);

        var nuget = packages.Where(p => p.Kind == PackageKind.NuGet).ToList();
        if (nuget.Count > 0)
        {
            foreach (var path in nuget.SelectMany(p => p.BuildProps.Append(p.Path)).Distinct(StringComparer.Ordinal))
                if (await files.ReadAsync(path) is { } text) files.Write(path, ReplaceVersions(path, text, notes));
            await ReferenceAsync(files, nuget, packageVersion ?? PackageVersion);
            await RemoveMinVerAsync(files, tree, nuget, notes);
            await NuGetConfigAsync(files, tree, owner, notes);
        }

        foreach (var package in packages.Where(p => p.Kind == PackageKind.Npm))
        {
            if (package.DeclaredVersion is { } declared && declared != Placeholder && await files.ReadAsync(package.Path) is { } json
                && NpmVersion().Match(json) is { Success: true } match && match.Groups[2].Value == declared)
                files.Write(package.Path, string.Concat(json.AsSpan(0, match.Groups[2].Index), Placeholder, json.AsSpan(match.Groups[2].Index + match.Groups[2].Length)));
            if (package.NgPackageDest is null)
                notes.Add($"`{package.Id}` is not built by ng-packagr, so the action stamps only `{package.Path}`: build it after the versioning step, or publish it from its source folder.");
        }

        var workflows = 0;
        foreach (var path in tree.Entries.Keys.Where(IsWorkflow).Order(StringComparer.Ordinal))
            if (await files.ReadAsync(path) is { } text && Workflow(path, text, actionSha, notes) is { } edited)
            {
                files.Write(path, edited);
                workflows++;
            }
        if (workflows == 0 && packages.Count > 0)
            notes.Add("No push-triggered workflow packs or publishes a package: add the versioning step before your publish steps by hand (see the action's README).");

        var coverageYml = await files.ReadAsync(CoverageYml.FileName);
        if (!DependencyOnboarding.HasTopLevelKey(coverageYml, "versioning"))
            files.Write(CoverageYml.FileName, coverageYml is null ? Section.TrimStart('\n') : coverageYml.TrimEnd('\n', '\r') + "\n" + Section);

        var written = files.Changed();
        return new VersioningOnboardingPlan(written, seeds, notes, Body(packages, seeds, taggedIds, written, notes, actionSha));
    }

    private static VersioningSeed? Seed(ProjectNode package, List<string> notes)
    {
        var declared = package.DeclaredVersion;
        if (declared is null or Placeholder)
        {
            notes.Add($"`{package.Id}` declares no version, so it gets no seed tag: push `{package.Id}/v<version>` for the version it last published, or the app plans its first release.");
            return null;
        }
        if (!Versioning.Versions.PackageVersion.TryParse(declared, out var version))
        {
            notes.Add($"`{package.Id}` is at `{declared}`, which is not a version the tags accept (`1.2.3` or `1.2.3-preview.4`), so it gets no seed tag: push `{package.Id}/v<version>` by hand.");
            return null;
        }
        return new VersioningSeed(package.Id!, package.Kind, version.ToString());
    }

    /// <summary>Every <c>Version</c>, <c>PackageVersion</c> and <c>VersionPrefix</c> property becomes the placeholder; <c>VersionSuffix</c> goes.</summary>
    internal static string ReplaceVersions(string path, string text, List<string> notes)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text.TrimStart('﻿'), LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            notes.Add($"`{path}` is not readable XML: replace its version with `{Placeholder}` by hand.");
            return text;
        }
        var lines = text.Split('\n').ToList();
        var drop = new List<int>();
        foreach (var element in document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
        {
            var name = element.Name.LocalName;
            var line = ((IXmlLineInfo)element).LineNumber - 1;
            if (name == "VersionSuffix" && SingleElementLine().IsMatch(lines[line])) drop.Add(line);
            if (!VersionProperties.Contains(name)) continue;
            if (element.Value.Contains("$(", StringComparison.Ordinal) || element.Value == Placeholder) continue;
            var replaced = Regex.Replace(lines[line], $@"<{name}(\s[^>]*)?>[^<]*</{name}>", m => name == "VersionPrefix"
                ? $"<Version{m.Groups[1].Value}>{Placeholder}</Version>"
                : $"<{name}{m.Groups[1].Value}>{Placeholder}</{name}>");
            if (replaced == lines[line]) notes.Add($"`{path}` line {line + 1}: `{name}` spans lines; replace it with `{Placeholder}` by hand.");
            lines[line] = replaced;
        }
        foreach (var line in drop.OrderDescending()) lines.RemoveAt(line);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The package reference, once per <c>Directory.Build.props</c> chain root (a new root file when a project has none), or
    /// as a <c>GlobalPackageReference</c> when the repository manages its versions centrally.
    /// </summary>
    private static async Task ReferenceAsync(Files files, List<ProjectNode> nuget, string version)
    {
        if (await files.ReadAsync("Directory.Packages.props") is { } central && CentralManagement().IsMatch(central))
        {
            if (!central.Contains(PackageId, StringComparison.Ordinal))
                files.Write("Directory.Packages.props", AddItem(central, $"<GlobalPackageReference Include=\"{PackageId}\" Version=\"{version}\" />"));
            return;
        }
        foreach (var root in nuget.Select(p => p.BuildProps.FirstOrDefault() ?? "Directory.Build.props").Distinct(StringComparer.Ordinal))
        {
            var reference = $"<PackageReference Include=\"{PackageId}\" Version=\"{version}\" PrivateAssets=\"all\" />";
            var text = await files.ReadAsync(root);
            if (text is null) files.Write(root, $"<Project>\n  <ItemGroup>\n    {reference}\n  </ItemGroup>\n</Project>\n");
            else if (!text.Contains(PackageId, StringComparison.Ordinal)) files.Write(root, AddItem(text, reference));
        }
    }

    /// <summary>
    /// The highest stable version in nuget.org's flat-container index (<see cref="PackageIndexUrl"/>), or null. A prerelease is
    /// never taken, and an unlisted version still counts: the index does not mark it, and unlisting is no reason to skip a fix.
    /// </summary>
    public static string? LatestStable(string indexJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(indexJson);
        if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        (SemVer Version, string Text)? best = null;
        foreach (var item in versions.EnumerateArray())
            if (item.GetString() is { } text && SemVer.TryParse(text, out var version) && version.Prerelease is null
                && (best is null || version.CompareTo(best.Value.Version) > 0))
                best = (version, text);
        return best?.Text;
    }

    /// <summary>Inserts an <c>ItemGroup</c> holding <paramref name="item"/> before <c>&lt;/Project&gt;</c>, in the file's own indentation.</summary>
    internal static string AddItem(string text, string item)
    {
        var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) return text;
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = Indentation().Match(text) is { Success: true } m ? m.Groups[1].Value : "  ";
        var before = text[..end].TrimEnd(' ', '\t');
        if (!before.EndsWith('\n')) before += eol;
        return $"{before}{eol}{indent}<ItemGroup>{eol}{indent}{indent}{item}{eol}{indent}</ItemGroup>{eol}{text[end..]}";
    }

    /// <summary>
    /// MinVer sets the version from the same tags its own way, after this package: its single-line references and settings go
    /// from the repository's own build. Another MSBuild file may ship to consumers, as MintPlayer.Dotnet.Tools' Vidyano SDK
    /// props do (#198): it is left alone, with a note.
    /// </summary>
    private static async Task RemoveMinVerAsync(Files files, IRepositoryTree tree, IReadOnlyList<ProjectNode> nuget, List<string> notes)
    {
        var ownBuild = new HashSet<string>(nuget.SelectMany(p => p.BuildProps.Append(p.Path)), StringComparer.Ordinal);
        foreach (var path in tree.Entries.Keys.Where(IsMSBuildFile).Order(StringComparer.Ordinal).Take(MaxMSBuildFiles))
        {
            if (await files.ReadAsync(path) is not { } text || !text.Contains("MinVer", StringComparison.Ordinal)) continue;
            if (!ownBuild.Contains(path) && !IsDirectoryFile(path))
            {
                notes.Add($"`{path}` mentions MinVer and was left alone: it is not a package project or a `Directory.*` file, so it may ship to consumers. If it is part of this repository's build, remove MinVer from it.");
                continue;
            }
            var lines = text.Split('\n');
            var kept = lines.Where(l => !MinVerLine().IsMatch(l)).ToList();
            if (kept.Count != lines.Length) files.Write(path, EmptyItemGroup().Replace(string.Join('\n', kept), ""));
            if (kept.Any(l => l.Contains("MinVer", StringComparison.Ordinal)))
                notes.Add($"`{path}` still mentions MinVer: remove it, or it sets the version after `{PackageId}`.");
        }
    }

    /// <summary>
    /// #544 D4: a repository restoring from GitHub Packages asks that feed for <see cref="PackageId"/> too, and one without
    /// Actions access to the package gets a 403. The root <c>nuget.config</c> maps the package to nuget.org (an exact id beats
    /// every prefix and <c>*</c>) and lists the feed under the URL setup-dotnet's <c>source-url</c> uses, so the credentials
    /// setup-dotnet writes next to the repository attach to it. The workflows stay as they are.
    /// </summary>
    private static async Task NuGetConfigAsync(Files files, IRepositoryTree tree, string? owner, List<string> notes)
    {
        var feeds = new List<string>();
        foreach (var path in tree.Entries.Keys.Where(IsWorkflow).Order(StringComparer.Ordinal))
        {
            if (await files.ReadAsync(path) is not { } workflow) continue;
            foreach (var source in SourceUrls(workflow).Where(u => u.Contains(GitHubFeedHost, StringComparison.OrdinalIgnoreCase)))
            {
                // The usual form (every onboarded repository's) names the owner through the context.
                var url = owner is null ? source : RepositoryOwner().Replace(source, owner);
                if (url.Contains("${{", StringComparison.Ordinal))
                    notes.Add($"`{path}` sets setup-dotnet's `source-url` from an expression: list that feed in the root `nuget.config` under the URL it resolves to, and map `{PackageId}` to nuget.org in its `<packageSourceMapping>`.");
                else if (!feeds.Contains(url, StringComparer.OrdinalIgnoreCase)) feeds.Add(url);
            }
        }

        var rootPath = tree.Entries.Keys.FirstOrDefault(p => p.Equals(NuGetConfigName, StringComparison.OrdinalIgnoreCase));
        foreach (var path in tree.Entries.Keys.Where(p => p != rootPath && Path.GetFileName(p).Equals(NuGetConfigName, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            if (await files.ReadAsync(path) is { } nested && nested.Contains(GitHubFeedHost, StringComparison.OrdinalIgnoreCase))
                notes.Add($"`{path}` lists GitHub Packages: map `{PackageId}` to nuget.org in its `<packageSourceMapping>` by hand, or restores below it ask GitHub Packages for it.");

        var root = rootPath is null ? null : await files.ReadAsync(rootPath);
        if (feeds.Count == 0 && root?.Contains(GitHubFeedHost, StringComparison.OrdinalIgnoreCase) != true) return;
        if (NuGetConfig(rootPath ?? NuGetConfigName, root, feeds, notes) is { } edited) files.Write(rootPath ?? NuGetConfigName, edited);
    }

    /// <summary>
    /// The <c>nuget.config</c> with every feed listed (matched as setup-dotnet matches it: a case-insensitive substring of a
    /// source's value) and <see cref="PackageId"/> mapped to nuget.org. A file without a mapping gets one that keeps today's
    /// resolution, every source taking <c>*</c>; null when nothing changes or the file cannot be edited safely.
    /// </summary>
    internal static string? NuGetConfig(string path, string? text, IReadOnlyList<string> feeds, List<string> notes)
    {
        text ??= "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n";
        XElement configuration;
        try
        {
            configuration = XDocument.Parse(text.TrimStart('﻿'), LoadOptions.SetLineInfo).Root!;
        }
        catch (XmlException)
        {
            notes.Add($"`{path}` is not readable XML: map `{PackageId}` to nuget.org in its `<packageSourceMapping>` by hand.");
            return null;
        }
        if (configuration.Name.LocalName != "configuration") return null;

        var lines = text.Split('\n').ToList();
        var unit = Indentation().Match(text) is { Success: true } m ? m.Groups[1].Value : "  ";
        var inserts = new SortedDictionary<int, List<string>>();
        bool Insert(XElement parent, int depth, IEnumerable<string> added)
        {
            if (parent.IsEmpty || ClosingLine(lines, parent) is not { } line)
            {
                notes.Add($"`{path}` line {((IXmlLineInfo)parent).LineNumber}: `<{parent.Name.LocalName}>` could not be extended; map `{PackageId}` to nuget.org by hand.");
                return false;
            }
            if (!inserts.TryGetValue(line, out var list)) inserts[line] = list = [];
            list.AddRange(added.Select(a => string.Concat(Enumerable.Repeat(unit, depth)) + a));
            return true;
        }

        var packageSources = configuration.Element("packageSources");
        var sources = packageSources?.Elements("add").Select(a => (Key: (string?)a.Attribute("key"), Url: (string?)a.Attribute("value")))
            .Where(a => a.Key is not null && a.Url is not null).Select(a => (Key: a.Key!, Url: a.Url!)).ToList() ?? [];
        var added = new List<(string Key, string Url)>();
        var nugetOrg = sources.FirstOrDefault(s => s.Url.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase)).Key;
        if (nugetOrg is null) added.Add((nugetOrg = "nuget.org", NuGetOrgSource));
        foreach (var feed in feeds.Where(f => !sources.Any(s => s.Url.Contains(f, StringComparison.OrdinalIgnoreCase))))
        {
            var key = "github";
            for (var n = 2; sources.Concat(added).Any(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); n++) key = $"github-{n}";
            added.Add((key, feed));
        }
        var adds = added.Select(a => new XElement("add", new XAttribute("key", a.Key), new XAttribute("value", a.Url)).ToString());
        if (added.Count > 0 && !(packageSources is null
            ? Insert(configuration, 1, ["<packageSources>", .. adds.Select(a => unit + a), "</packageSources>"])
            : Insert(packageSources, 2, adds)))
            return null;

        var exact = $"<package pattern=\"{PackageId}\" />";
        var mapping = configuration.Element("packageSourceMapping");
        if (mapping is null)
        {
            var block = new List<string> { "<packageSourceMapping>" };
            foreach (var key in sources.Concat(added).Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                block.Add($"{unit}{new XElement("packageSource", new XAttribute("key", key)).ToString().Replace(" />", ">", StringComparison.Ordinal)}");
                block.Add($"{unit}{unit}<package pattern=\"*\" />");
                if (key == nugetOrg) block.Add($"{unit}{unit}{exact}");
                block.Add($"{unit}</packageSource>");
            }
            block.Add("</packageSourceMapping>");
            if (!Insert(configuration, 1, block)) return null;
        }
        else if (!mapping.Descendants("package").Any(p => string.Equals((string?)p.Attribute("pattern"), PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            var mapped = mapping.Elements("packageSource").FirstOrDefault(s => string.Equals((string?)s.Attribute("key"), nugetOrg, StringComparison.OrdinalIgnoreCase));
            var inserted = mapped is null
                ? Insert(mapping, 2, [new XElement("packageSource", new XAttribute("key", nugetOrg)).ToString().Replace(" />", ">", StringComparison.Ordinal), unit + exact, "</packageSource>"])
                : Insert(mapped, 3, [exact]);
            if (!inserted) return null;
        }
        if (inserts.Count == 0) return null;

        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r" : "";
        foreach (var (line, insert) in inserts.Reverse()) lines.InsertRange(line, insert.Select(l => l + eol));
        return string.Join('\n', lines);
    }

    /// <summary>The line of <paramref name="element"/>'s closing tag, when it stands on a line of its own.</summary>
    private static int? ClosingLine(List<string> lines, XElement element)
    {
        // nuget.config nests no element in one of the same name, so the first closing tag is the element's.
        var close = $"</{element.Name.LocalName}>";
        for (var l = ((IXmlLineInfo)element).LineNumber - 1; l < lines.Count; l++)
        {
            if (lines[l].Trim() == close) return l;
            if (lines[l].Contains(close, StringComparison.Ordinal)) return null;
        }
        return null;
    }

    /// <summary>Every setup-dotnet step's <c>source-url</c> in the workflow.</summary>
    private static IEnumerable<string> SourceUrls(string workflow)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(workflow));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            yield break;
        }
        if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root || Child(root, "jobs") is not YamlMappingNode jobs) yield break;
        foreach (var job in jobs.Children.Values)
            if (Child(job, "steps") is YamlSequenceNode steps)
                foreach (var step in steps.Children)
                    if (Scalar(step, "uses")?.StartsWith("actions/setup-dotnet", StringComparison.OrdinalIgnoreCase) == true
                        && Child(step, "with") is { } with && Scalar(with, "source-url") is { Length: > 0 } url)
                        yield return url;
    }

    private static bool IsDirectoryFile(string path)
        => Path.GetFileName(path) is var name && (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase));

    private static bool IsMSBuildFile(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkflow(string path)
        => path.StartsWith(".github/workflows/", StringComparison.Ordinal) && path.IndexOf('/', ".github/workflows/".Length) < 0
            && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    /// <summary>One edit of a workflow, against the original line numbers: a whole-line replacement, or lines inserted before one.</summary>
    private sealed record LineEdit(int Line, string? Replace, IReadOnlyList<string>? Insert);

    /// <summary>
    /// The workflow with the versioning step in front of each push-triggered job's first pack or publish step, the job's
    /// permissions widened to <c>contents: write</c> and <c>id-token: write</c>, <c>--no-build</c> dropped from its pack (with
    /// a comment saying why) and <c>--skip-duplicate</c> added to its NuGet push, and a <c>concurrency</c> group when the
    /// workflow has none; null when the workflow needs nothing.
    /// </summary>
    internal static string? Workflow(string path, string text, string? actionSha, List<string> notes)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode mapping) return null;
            root = mapping;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            notes.Add($"`{path}` is not readable YAML: add the versioning step by hand.");
            return null;
        }
        if (Child(root, "jobs") is not YamlMappingNode jobs) return null;

        var publishing = jobs.Children.Where(j => j.Value is YamlMappingNode job && PublishStep(job) is not null).ToList();
        if (publishing.Count == 0) return null;
        if (!OnPush(Child(root, "on")))
        {
            notes.Add($"`{path}` publishes, but not on a push: the action versions only branch pushes, so the versioning step goes in front of the publish of the push workflow (none was added here).");
            return null;
        }

        var lines = text.Split('\n');
        var edits = new List<LineEdit>();
        foreach (var (key, value) in publishing)
        {
            var job = (YamlMappingNode)value;
            var steps = (YamlSequenceNode)Child(job, "steps")!;
            if (steps.Children.Any(s => Scalar(s, "uses")?.Contains("versioning-action", StringComparison.Ordinal) == true)) continue;
            var first = PublishStep(job)!.Value;
            TestOrder(path, (YamlScalarNode)key, job, jobs, first, notes);

            // The step, above the first pack or publish step's comment block, separated like its neighbours.
            var stepLine = Line(steps.Children[first].Start) - 1;
            var dash = lines[stepLine].IndexOf('-');
            var keyIndent = new string(' ', (int)steps.Children[first].Start.Column - 1);

            // #544 D3: the pack builds again, so the versions decided after the tests reach the assemblies. Added before
            // the versioning step, so on the same line it lands below it.
            if (Enumerable.Range(first, steps.Children.Count - first).FirstOrDefault(i => Scalar(steps.Children[i], "run")?.Contains("dotnet pack", StringComparison.Ordinal) == true, -1) is var pack and >= 0)
            {
                var pad = new string(' ', dash);
                edits.Add(new LineEdit(Line(steps.Children[pack].Start) - 1, null, [
                    $"{pad}# No --no-build: the versions are decided after the tests, so pack builds again to stamp them into the",
                    $"{pad}# assemblies (--no-build also skips restore, which leaves the nuspec's dependency versions stale).",
                ]));
            }

            var at = stepLine;
            while (at > 0 && lines[at - 1].TrimStart().StartsWith('#')) at--;
            var step = new List<string>
            {
                $"{new string(' ', dash)}- name: {StepName}",
                $"{keyIndent}id: versioning",
                $"{keyIndent}uses: {ActionPath}@{actionSha ?? ActionTag}{(actionSha is null ? "" : $" # {ActionTag}")}",
            };
            if (at > 0 && lines[at - 1].Trim().Length == 0) step.Add("");
            edits.Add(new LineEdit(at, null, step));

            for (var i = first; i < steps.Children.Count; i++)
            {
                if (Scalar(steps.Children[i], "run") is not { } run) continue;
                // A step's lines run to the next step; comments between them are left alone.
                var from = Line(steps.Children[i].Start) - 1;
                var to = i + 1 < steps.Children.Count ? Line(steps.Children[i + 1].Start) - 2 : Line(steps.Children[i].End) - 1;
                for (var l = from; l <= to && l < lines.Length; l++)
                {
                    var line = lines[l];
                    if (line.TrimStart().StartsWith('#')) continue;
                    if (run.Contains("dotnet pack", StringComparison.Ordinal)) line = NoBuild().Replace(line, "");
                    if (run.Contains("dotnet nuget push", StringComparison.Ordinal) && !run.Contains("--skip-duplicate", StringComparison.Ordinal)
                        && line.Contains("dotnet nuget push", StringComparison.Ordinal))
                        line = LineEnd().Replace(line, " --skip-duplicate$1", 1);
                    if (line != lines[l]) edits.Add(new LineEdit(l, line, null));
                }
                if (RawNpmPublish().IsMatch(run))
                    notes.Add($"`{path}` job `{key}` runs `npm publish` itself, which fails on a package this commit did not release: use `MintPlayer/github-actions/publish-npm-packages`, which skips versions the registry has.");
            }

            Permissions(path, root, (YamlScalarNode)key, job, lines, edits, notes);
        }
        if (edits.Count == 0) return null;

        // #544 D3: a newer push cancels the older run. A run cancelled after its tags were pushed leaves tags without
        // packages, which is harmless: the newer run packs them. A workflow's own concurrency is left alone.
        if (Child(root, "concurrency") is null && root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == "jobs") is { } jobsKey
            && jobsKey.Start.Column == 1)
        {
            var unit = new string(' ', Math.Max(2, (int)jobs.Children.Keys.First().Start.Column - 1));
            edits.Add(new LineEdit(Line(jobsKey.Start) - 1, null, [
                "concurrency:",
                unit + "group: ${{ github.workflow }}-${{ github.ref }}",
                unit + "cancel-in-progress: true",
                "",
            ]));
        }

        var result = lines.ToList();
        foreach (var edit in edits.Where(e => e.Replace is not null)) result[edit.Line] = edit.Replace!;
        foreach (var edit in edits.Where(e => e.Insert is not null).OrderByDescending(e => e.Line))
        {
            var eol = result[Math.Min(edit.Line, result.Count - 1)].EndsWith('\r') ? "\r" : "";
            result.InsertRange(edit.Line, edit.Insert!.Select(l => l + eol));
        }
        return string.Join('\n', result);
    }

    /// <summary>
    /// #544 D3: the tags are pushed only once the tests passed, so a test that runs after the versioning step, or in a job
    /// the publishing job does not wait for, is reported: the rewrite does not move steps or jobs.
    /// </summary>
    private static void TestOrder(string path, YamlScalarNode key, YamlMappingNode job, YamlMappingNode jobs, int first, List<string> notes)
    {
        var steps = ((YamlSequenceNode)Child(job, "steps")!).Children;
        if (steps.Skip(first).Any(s => Scalar(s, "run") is { } run && TestRun().IsMatch(run)))
            notes.Add($"`{path}` job `{key.Value}` runs tests after it packs: move them above the versioning step, which pushes the tags.");

        var awaited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<YamlNode?>([Child(job, "needs")]);
        while (pending.TryPop(out var needs))
        {
            var names = needs switch
            {
                YamlScalarNode one => [one.Value!],
                YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(n => n.Value!).ToArray(),
                _ => Array.Empty<string>(),
            };
            foreach (var name in names.Where(awaited.Add))
                if (Child(jobs, name) is { } needed) pending.Push(Child(needed, "needs"));
        }
        foreach (var (otherKey, other) in jobs.Children)
        {
            var name = ((YamlScalarNode)otherKey).Value!;
            if (name == key.Value || awaited.Contains(name) || Child(other, "steps") is not YamlSequenceNode otherSteps) continue;
            if (otherSteps.Children.Any(s => Scalar(s, "run") is { } run && TestRun().IsMatch(run)))
                notes.Add($"`{path}` job `{key.Value}` does not wait for job `{name}`, which runs tests: add it to `needs`, so a failing test pushes no tags.");
        }
    }

    /// <summary>The job's token gets <c>contents: write</c> (the tags) and <c>id-token: write</c> (the app's proof of the caller).</summary>
    private static void Permissions(string path, YamlMappingNode root, YamlScalarNode key, YamlMappingNode job, string[] lines, List<LineEdit> edits, List<string> notes)
    {
        string[] needed = ["contents", "id-token"];
        switch (Child(job, "permissions"))
        {
            case YamlMappingNode own:
                var indent = own.Children.Count > 0 ? (int)own.Children.First().Key.Start.Column - 1 : 0;
                var last = own.Children.Count > 0 ? own.Children.Max(c => Line(c.Value.Start)) - 1 : -1;
                foreach (var name in needed)
                {
                    if (own.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == name) is { Key: not null } entry)
                    {
                        if (entry.Value is YamlScalarNode { Value: not "write" } scalar && Line(scalar.Start) == Line(scalar.End))
                        {
                            var l = Line(scalar.Start) - 1;
                            var col = (int)scalar.Start.Column - 1;
                            edits.Add(new LineEdit(l, lines[l][..col] + "write" + lines[l][((int)scalar.End.Column - 1)..], null));
                        }
                    }
                    else if (last >= 0 && own.Style != YamlDotNet.Core.Events.MappingStyle.Flow)
                        edits.Add(new LineEdit(last + 1, null, [$"{new string(' ', indent)}{name}: write"]));
                    else notes.Add($"`{path}` job `{key.Value}`: add `{name}: write` to its permissions by hand.");
                }
                return;
            case YamlScalarNode { Value: "write-all" }:
                return;
            case YamlScalarNode other:
                notes.Add($"`{path}` job `{key.Value}` has `permissions: {other.Value}`: it needs `contents: write` and `id-token: write`.");
                return;
        }

        // A job-level block replaces the workflow's, so it repeats the workflow's entries.
        var entries = new List<(string Name, string Value)>();
        var stepsKey = job.Children.First(c => ((YamlScalarNode)c.Key).Value == "steps").Key;
        switch (Child(root, "permissions"))
        {
            case YamlScalarNode { Value: "write-all" }:
                return;
            case YamlMappingNode workflow:
                entries.AddRange(workflow.Children.Select(c => (((YamlScalarNode)c.Key).Value!, (c.Value as YamlScalarNode)?.Value ?? "read")));
                break;
            case YamlScalarNode other:
                notes.Add($"`{path}` has `permissions: {other.Value}`: job `{key.Value}` needs `contents: write` and `id-token: write`.");
                return;
            default:
                // GitHub Packages needs packages: write; a NuGet push to a source other than nuget.org is taken to go there.
                var runs = ((YamlSequenceNode)Child(job, "steps")!).Children.Select(s => Scalar(s, "run")).OfType<string>().ToList();
                if (runs.Any(r => r.Contains("pkg.github.com", StringComparison.Ordinal)
                    || r.Contains("dotnet nuget push", StringComparison.Ordinal) && !r.Contains("api.nuget.org", StringComparison.Ordinal)))
                    entries.Add(("packages", "write"));
                notes.Add($"`{path}` job `{key.Value}` had the repository's default token permissions; it now lists its own. Add any other permission it needs.");
                break;
        }
        foreach (var name in needed)
        {
            entries.RemoveAll(e => e.Name == name);
            entries.Add((name, "write"));
        }
        var at = Line(stepsKey.Start) - 1;
        var pad = new string(' ', (int)stepsKey.Start.Column - 1);
        var unit = Math.Max(2, (int)stepsKey.Start.Column - (int)key.Start.Column);
        edits.Add(new LineEdit(at, null, [$"{pad}permissions:", .. entries.Select(e => $"{pad}{new string(' ', unit)}{e.Name}: {e.Value}"), ""]));
    }

    /// <summary>The index of the job's first step that packs or publishes a package, or null.</summary>
    private static int? PublishStep(YamlMappingNode job)
    {
        if (Child(job, "steps") is not YamlSequenceNode steps) return null;
        for (var i = 0; i < steps.Children.Count; i++)
        {
            if (Scalar(steps.Children[i], "run") is { } run && (run.Contains("dotnet pack", StringComparison.Ordinal)
                || run.Contains("dotnet nuget push", StringComparison.Ordinal) || RawNpmPublish().IsMatch(run)))
                return i;
            if (Scalar(steps.Children[i], "uses")?.StartsWith("MintPlayer/github-actions/publish-npm-packages", StringComparison.OrdinalIgnoreCase) == true)
                return i;
        }
        return null;
    }

    private static bool OnPush(YamlNode? on) => on switch
    {
        YamlScalarNode scalar => scalar.Value == "push",
        YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Any(s => s.Value == "push"),
        YamlMappingNode mapping => mapping.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == "push"),
        _ => false,
    };

    private static YamlNode? Child(YamlNode node, string key)
        => node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static int Line(YamlDotNet.Core.Mark mark) => (int)mark.Line;

    private static string Body(List<ProjectNode> packages, List<VersioningSeed> seeds, IReadOnlySet<string> taggedIds, IReadOnlyList<GitWriterFile> files,
        List<string> notes, string? actionSha)
    {
        var body = new StringBuilder();
        body.Append("This switches the repository to tag-driven package versions (CodeCoverage package versioning). On every push to the ")
            .Append("default branch, the versioning step decides which packages changed, tags each release `<package id>/v<version>`, and ")
            .Append("writes the npm versions. NuGet packages read their version from the tags through `").Append(PackageId).Append("`. ")
            .Append("Every package is packed and published on every run: an unchanged one keeps the version it already published, which the push skips.\n\n");
        body.Append("The step calls `").Append(ActionPath).Append('`').Append(actionSha is null ? $" at `{ActionTag}`" : $", pinned to `{actionSha}` (`{ActionTag}`)").Append(".\n\n");

        body.Append("## Packages\n\n| Package | Seed tag |\n|---|---|\n");
        foreach (var package in packages)
        {
            var seed = seeds.FirstOrDefault(s => s.PackageId == package.Id);
            var tag = seed is not null ? $"`{VersionTag.Prefix(seed.PackageId)}{seed.Version}`"
                : taggedIds.Contains(package.Id!) ? "already tagged" : "none (see below)";
            body.Append("| `").Append(package.Id).Append("` | ").Append(tag).Append(" |\n");
        }
        body.Append("\nWhen this merges, the app pushes the seed tags at the merge commit, so each package continues from the version it has now. ")
            .Append("The versions in the project files become `").Append(Placeholder).Append("`: the tags are the only place a version lives.\n\n");

        body.Append("## Files\n\n");
        foreach (var file in files) body.Append("- `").Append(file.Path).Append("`\n");

        if (notes.Count > 0)
        {
            body.Append("\n## Needs a look\n\n");
            foreach (var note in notes) body.Append("- ").Append(note).Append('\n');
        }
        return body.ToString();
    }

    /// <summary>
    /// The repository's files as the plan reads and edits them: a file the plan wrote is read back as written. The plan
    /// edits text without a byte order mark, and a file that had one gets it back when written.
    /// </summary>
    private sealed class Files(IRepositoryTree tree, CancellationToken cancellationToken)
    {
        private const char ByteOrderMark = '﻿';
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> written = new(StringComparer.Ordinal);
        private readonly HashSet<string> withBom = new(StringComparer.Ordinal);

        public async Task<string?> ReadAsync(string path)
        {
            if (written.TryGetValue(path, out var text)) return text;
            if (!original.TryGetValue(path, out var read))
            {
                var bytes = await tree.ReadAsync(path, MaxFileBytes, cancellationToken);
                read = bytes is null ? null : Encoding.UTF8.GetString(bytes);
                if (read?.StartsWith(ByteOrderMark) == true)
                {
                    withBom.Add(path);
                    read = read.TrimStart(ByteOrderMark);
                }
                original[path] = read;
            }
            return read;
        }

        public void Write(string path, string text)
        {
            if (original.GetValueOrDefault(path) == text) written.Remove(path);
            else written[path] = text;
        }

        public IReadOnlyList<GitWriterFile> Changed() => [.. written.OrderBy(w => w.Key, StringComparer.Ordinal)
            .Select(w => new GitWriterFile(w.Key, withBom.Contains(w.Key) ? ByteOrderMark + w.Value : w.Value))];
    }

    [GeneratedRegex(@"^(\s*""version""\s*:\s*"")([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex NpmVersion();

    [GeneratedRegex(@"^\s*<VersionSuffix(\s[^>]*)?>[^<]*</VersionSuffix>\s*\r?$")]
    private static partial Regex SingleElementLine();

    [GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase)]
    private static partial Regex CentralManagement();

    [GeneratedRegex(@"^([ \t]+)<", RegexOptions.Multiline)]
    private static partial Regex Indentation();

    [GeneratedRegex(@"^\s*(<(PackageReference|PackageVersion|GlobalPackageReference)\s+Include=""MinVer""[^>]*/>|<MinVer\w*>[^<]*</MinVer\w*>)\s*\r?$")]
    private static partial Regex MinVerLine();

    [GeneratedRegex(@"\n[ \t]*<ItemGroup>\r?\n[ \t]*</ItemGroup>\r?(?=\n)")]
    private static partial Regex EmptyItemGroup();

    [GeneratedRegex(@"\$\{\{\s*github\.repository_owner\s*\}\}")]
    private static partial Regex RepositoryOwner();

    [GeneratedRegex(@"\bdotnet\s+test\b|\b(npm|pnpm|yarn)\s+(run\s+)?test\b|\bnx\b.*\btest\b")]
    private static partial Regex TestRun();

    [GeneratedRegex(@"[ \t]+--no-build\b")]
    private static partial Regex NoBuild();

    [GeneratedRegex(@"(\s*\\?\r?)$")]
    private static partial Regex LineEnd();

    [GeneratedRegex(@"(^|[\s;&|])(npm|pnpm|yarn)\s+publish\b|nx\s+release\s+publish")]
    private static partial Regex RawNpmPublish();
}
