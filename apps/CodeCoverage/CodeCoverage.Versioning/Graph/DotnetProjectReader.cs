using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// Reads <c>.csproj</c> files statically (PRD D5): no MSBuild, no evaluation of conditions, targets or property
/// functions. Unconditional properties come from the <c>Directory.Build.props</c> chain and the project, with
/// <c>$(Name)</c> substituted; anything still holding <c>$(</c> afterwards counts as unknown, and the build's release
/// manifest fills it in. XML is read with DTDs prohibited and no resolver, under a size cap.
/// </summary>
public sealed partial class DotnetProjectReader(IRepositoryTree tree, List<string> warnings)
{
	private static readonly string[] RepositoryInputs = ["global.json", "nuget.config", "NuGet.Config", "NuGet.config"];

	[GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)", RegexOptions.CultureInvariant)]
	private static partial Regex PropertyReference();

	private readonly Dictionary<string, XDocument?> documents = new(StringComparer.Ordinal);

	public async Task<ProjectNode?> ReadAsync(string path, CancellationToken cancellationToken)
	{
		var project = await LoadAsync(path, cancellationToken);
		if (project is null) return null;
		var directory = RepoPath.Directory(path);
		var name = Path.GetFileNameWithoutExtension(RepoPath.FileName(path));

		var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["MSBuildProjectName"] = name,
			["MSBuildProjectDirectory"] = directory,
		};
		var inputs = new List<string> { directory.Length == 0 ? path : directory };
		var imported = new List<XDocument>();

		var buildProps = await ChainAsync(directory, "Directory.Build.props", cancellationToken);
		foreach (var props in buildProps)
		{
			inputs.Add(props);
			if (await LoadAsync(props, cancellationToken) is { } document) imported.Add(document);
		}
		foreach (var document in imported) CollectProperties(document, properties);
		CollectProperties(project, properties);

		var targets = await ChainAsync(directory, "Directory.Build.targets", cancellationToken);
		inputs.AddRange(targets);
		foreach (var file in targets)
			if (await LoadAsync(file, cancellationToken) is { } document) imported.Add(document);

		var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (Nearest(directory, "Directory.Packages.props") is { } packagesProps)
		{
			inputs.Add(packagesProps);
			if (await LoadAsync(packagesProps, cancellationToken) is { } document)
				foreach (var item in Items(document, "PackageVersion"))
					if (item.Attribute("Include")?.Value is { } packageId && item.Attribute("Version")?.Value is { } version) centralVersions[packageId] = version;
		}
		foreach (var ancestor in RepoPath.Ancestry(directory))
			foreach (var file in RepositoryInputs)
				if (tree.IsFile(RepoPath.Combine(ancestor, file))) inputs.Add(RepoPath.Combine(ancestor, file));

		string? Property(string key) => properties.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

		var id = Property("PackageId") ?? Property("AssemblyName") ?? name;
		if (id.Contains("$(")) { warnings.Add($"{path}: PackageId '{id}' uses a property the parser cannot resolve."); id = null!; }

		var tfmText = Property("TargetFrameworks") ?? Property("TargetFramework") ?? "";
		IReadOnlyList<string> tfms = tfmText.Contains("$(") ? [] : tfmText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var dependencies = new List<PackageDependency>();
		foreach (var item in imported.Append(project).SelectMany(d => Items(d, "PackageReference")))
		{
			if (item.Attribute("Include")?.Value is not { Length: > 0 } include) continue;
			var version = Attribute(item, "VersionOverride") ?? Attribute(item, "Version") ?? centralVersions.GetValueOrDefault(include);
			var isPrivate = string.Equals(Attribute(item, "PrivateAssets")?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
			dependencies.Add(new PackageDependency(include, version, isPrivate ? DependencyKind.Dev : DependencyKind.Runtime));
		}

		var references = new List<string>();
		foreach (var item in Items(project, "ProjectReference"))
		{
			if (item.Attribute("Include")?.Value is not { Length: > 0 } include) continue;
			var resolved = RepoPath.Resolve(directory, StripThisFileDirectory(include))
				?? throw new GraphException($"{path}: the ProjectReference '{include}' points outside the repository.");
			if (tree.Entries.TryGetValue(resolved, out var entry) && entry.IsSymlink)
				throw new GraphException($"{path}: the ProjectReference '{include}' is a symbolic link.");
			if (!tree.IsFile(resolved)) { warnings.Add($"{path}: the ProjectReference '{include}' does not exist."); continue; }
			references.Add(resolved);
		}

		foreach (var kind in new[] { "Compile", "None", "Content", "EmbeddedResource" })
			foreach (var item in Items(project, kind))
				if (item.Attribute("Include")?.Value is { } include && include.Contains("..") && LinkedInput(directory, include) is { } linked)
					inputs.Add(linked);

		// A project's relative <Import Project="..\build\Pack.targets" /> shapes the package as much as its sources (S1).
		foreach (var import in project.Descendants().Where(e => e.Name.LocalName == "Import"))
			if (import.Attribute("Project")?.Value is { } importPath && (!importPath.Contains("$(") || importPath.StartsWith("$(MSBuildThisFileDirectory)", StringComparison.OrdinalIgnoreCase))
				&& RepoPath.Resolve(directory, StripThisFileDirectory(importPath)) is { } resolvedImport && tree.IsFile(resolvedImport))
				inputs.Add(resolvedImport);

		return new ProjectNode
		{
			Path = path,
			Kind = PackageKind.NuGet,
			Id = id,
			Publishable = IsPackable(project, Property("IsPackable"), Property("IsTestProject"), dependencies),
			TargetFrameworks = tfms,
			AssemblyName = Property("AssemblyName") is { } assemblyName ? (assemblyName.Contains("$(") ? null : assemblyName) : name,
			ProjectReferences = references,
			Dependencies = dependencies,
			Inputs = inputs.Distinct(StringComparer.Ordinal).ToList(),
			DeclaredVersion = Property("PackageVersion") ?? Property("Version")
				?? (Property("VersionPrefix") is { } prefix ? (Property("VersionSuffix") is { } suffix ? $"{prefix}-{suffix}" : prefix) : null),
			BuildProps = buildProps,
		};

		string? Attribute(XElement item, string key)
			=> item.Attribute(key)?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == key)?.Value;
	}

	private static bool IsPackable(XDocument project, string? isPackable, string? isTestProject, List<PackageDependency> dependencies)
	{
		if (bool.TryParse(isPackable, out var explicitValue)) return explicitValue;
		if (bool.TryParse(isTestProject, out var test) && test) return false;
		if (dependencies.Any(d => d.Name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))) return false;
		// The Web SDK defaults IsPackable to false; a web app is deployed, not packed.
		var sdk = project.Root?.Attribute("Sdk")?.Value ?? "";
		return !sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A linked item outside the project (<c>..\Shared\*.cs</c>) as an input: cut at the first wildcard or property, and
	/// at <c>bin</c>/<c>obj</c>, which stand for the project that owns them (S1).
	/// </summary>
	private string? LinkedInput(string directory, string include)
	{
		var text = StripThisFileDirectory(include).Replace('\\', '/');
		var segments = new List<string>();
		foreach (var segment in text.Split('/'))
		{
			if (segment.Contains('*') || segment.Contains('?') || segment.Contains("$(") || segment is "bin" or "obj") break;
			segments.Add(segment);
		}
		if (segments.Count == 0) return null;
		var resolved = RepoPath.Resolve(directory, string.Join('/', segments));
		if (resolved is null) { warnings.Add($"{directory}: the linked item '{include}' points outside the repository and is ignored."); return null; }
		return resolved;
	}

	private static string StripThisFileDirectory(string include)
		=> include.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.OrdinalIgnoreCase)
			.Replace("$(MSBuildProjectDirectory)/", "", StringComparison.OrdinalIgnoreCase)
			.Replace("$(MSBuildProjectDirectory)\\", "", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The nearest <paramref name="fileName"/> above <paramref name="directory"/>, then any parent it imports, either by
	/// <c>GetPathOfFileAbove</c>/<c>GetDirectoryNameOfFileAbove</c> or by a plain relative path. Root first.
	/// </summary>
	private async Task<List<string>> ChainAsync(string directory, string fileName, CancellationToken cancellationToken)
	{
		var chain = new List<string>();
		var current = Nearest(directory, fileName);
		while (current is not null && chain.Count < Limits.MaxPropsChain && !chain.Contains(current))
		{
			chain.Add(current);
			var document = await LoadAsync(current, cancellationToken);
			string? next = null;
			foreach (var import in document?.Descendants().Where(e => e.Name.LocalName == "Import") ?? [])
			{
				var project = import.Attribute("Project")?.Value ?? "";
				if (project.Contains("FileAbove", StringComparison.Ordinal) && project.Contains(fileName, StringComparison.OrdinalIgnoreCase))
				{
					var parent = RepoPath.Directory(RepoPath.Directory(current));
					next = RepoPath.Directory(current).Length == 0 ? null : Nearest(parent, fileName);
				}
				else if (!project.Contains("$(", StringComparison.Ordinal) || project.StartsWith("$(MSBuildThisFileDirectory)", StringComparison.OrdinalIgnoreCase))
				{
					var resolved = RepoPath.Resolve(RepoPath.Directory(current), StripThisFileDirectory(project));
					if (resolved is not null && RepoPath.FileName(resolved).Equals(fileName, StringComparison.OrdinalIgnoreCase) && tree.IsFile(resolved)) next = resolved;
				}
			}
			current = next;
		}
		chain.Reverse();
		return chain;
	}

	private string? Nearest(string directory, string fileName)
		=> RepoPath.Ancestry(directory).Reverse().Select(d => RepoPath.Combine(d, fileName)).FirstOrDefault(tree.IsFile);

	private static void CollectProperties(XDocument document, Dictionary<string, string> properties)
	{
		foreach (var group in document.Root?.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null) ?? [])
			foreach (var property in group.Elements().Where(e => e.Attribute("Condition") is null))
				properties[property.Name.LocalName] = Substitute(property.Value.Trim(), properties);
	}

	private static string Substitute(string value, Dictionary<string, string> properties)
		=> PropertyReference().Replace(value, m => properties.TryGetValue(m.Groups[1].Value, out var known) ? known : m.Value);

	private static IEnumerable<XElement> Items(XDocument document, string kind)
		=> document.Root?.Elements().Where(e => e.Name.LocalName == "ItemGroup" && e.Attribute("Condition") is null)
			.SelectMany(g => g.Elements().Where(e => e.Name.LocalName == kind)) ?? [];

	private async Task<XDocument?> LoadAsync(string path, CancellationToken cancellationToken)
	{
		if (documents.TryGetValue(path, out var cached)) return cached;
		XDocument? document = null;
		if (await tree.ReadAsync(path, Limits.MaxManifestBytes, cancellationToken) is { } bytes)
		{
			var settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = Limits.MaxManifestBytes,
				IgnoreComments = true,
			};
			try
			{
				using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
				document = XDocument.Load(reader);
			}
			catch (XmlException e)
			{
				warnings.Add($"{path}: not readable XML ({e.Message}).");
			}
		}
		documents[path] = document;
		return document;
	}
}
