using System.Text.Json;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// The resolved versions of an npm <c>package-lock.json</c> (lockfile v2/v3): the <c>packages</c> map keyed by
/// <c>node_modules/&lt;name&gt;</c> paths. The major of an Angular library is read from here, never from a range (D7).
/// </summary>
public sealed class NpmLockfile(IReadOnlyDictionary<string, string> versions)
{
	public IReadOnlyDictionary<string, string> Versions { get; } = versions;

	/// <summary>The version node would load for <paramref name="name"/> from <paramref name="directory"/>: nearest <c>node_modules</c> first.</summary>
	public string? Resolve(string name, string directory)
	{
		foreach (var ancestor in RepoPath.Ancestry(directory).Reverse())
			if (Versions.TryGetValue(RepoPath.Combine(ancestor, "node_modules/" + name), out var version)) return version;
		return null;
	}

	public static NpmLockfile Parse(byte[] json)
	{
		using var document = JsonDocument.Parse(json, NpmProjectReader.Options);
		var versions = new Dictionary<string, string>(StringComparer.Ordinal);
		if (document.RootElement.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
			foreach (var package in packages.EnumerateObject())
				if (package.Name.Contains("node_modules/", StringComparison.Ordinal)
					&& package.Value.ValueKind == JsonValueKind.Object
					&& package.Value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
					versions[package.Name] = version.GetString()!;
		return new NpmLockfile(versions);
	}
}

/// <summary>
/// Reads npm packages statically (PRD D5): <c>package.json</c> for the name, privacy, version and dependencies, and
/// <c>ng-package.json</c> for an Angular library's output folder. JSON is read with a depth limit under a size cap.
/// </summary>
public sealed class NpmProjectReader(IRepositoryTree tree, List<string> warnings)
{
	internal static readonly JsonDocumentOptions Options = new() { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	/// <summary>
	/// The <c>package.json</c> files that are packages of the repository: the root, its <c>workspaces</c>, and every
	/// directory an Nx <c>project.json</c> or an <c>ng-package.json</c> marks as a project. Never below <c>node_modules</c>.
	/// </summary>
	public async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken cancellationToken)
	{
		var found = new SortedSet<string>(StringComparer.Ordinal);
		if (!tree.IsFile("package.json")) return [];
		found.Add("package.json");

		var globs = new List<PathGlob>();
		if (await ReadJsonAsync("package.json", cancellationToken) is { } root && root.RootElement.TryGetProperty("workspaces", out var workspaces))
		{
			var list = workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var packages) ? packages : workspaces;
			if (list.ValueKind == JsonValueKind.Array)
				foreach (var item in list.EnumerateArray())
					if (item.ValueKind == JsonValueKind.String && item.GetString() is { } glob)
					{
						if (glob.StartsWith('!')) continue;
						try { globs.Add(new PathGlob(glob.TrimEnd('/') + "/package.json")); }
						catch (GraphException e) { warnings.Add($"package.json: workspace '{glob}' ignored ({e.Message})."); }
					}
		}

		foreach (var (path, entry) in tree.Entries)
		{
			if (!entry.IsFile || IsUnderNodeModules(path)) continue;
			var fileName = RepoPath.FileName(path);
			if (fileName == "package.json" && globs.Any(g => g.Matches(path))) found.Add(path);
			else if (fileName is "project.json" or "ng-package.json")
			{
				var manifest = RepoPath.Combine(RepoPath.Directory(path), "package.json");
				if (tree.IsFile(manifest)) found.Add(manifest);
			}
			if (found.Count > Limits.MaxProjects) throw new GraphException($"The repository has more than {Limits.MaxProjects} npm projects.");
		}
		return found.ToList();
	}

	public async Task<ProjectNode?> ReadAsync(string path, CancellationToken cancellationToken)
	{
		using var document = await ReadJsonAsync(path, cancellationToken);
		if (document?.RootElement is not { ValueKind: JsonValueKind.Object } manifest) return null;
		var directory = RepoPath.Directory(path);

		var name = String(manifest, "name");
		if (name is not null && !Validation.Identifiers.IsNpmName(name)) { warnings.Add($"{path}: '{name}' is not a valid npm package name."); name = null; }
		var isPrivate = manifest.TryGetProperty("private", out var privateValue) && privateValue.ValueKind == JsonValueKind.True;

		var dependencies = new List<PackageDependency>();
		foreach (var (section, kind) in new[] { ("dependencies", DependencyKind.Runtime), ("optionalDependencies", DependencyKind.Runtime), ("peerDependencies", DependencyKind.Peer), ("devDependencies", DependencyKind.Dev) })
			if (manifest.TryGetProperty(section, out var map) && map.ValueKind == JsonValueKind.Object)
				foreach (var dependency in map.EnumerateObject())
					dependencies.Add(new PackageDependency(dependency.Name, dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString() : null, kind));

		var inputs = new List<string> { directory.Length == 0 ? path : directory };
		string? dest = null;
		var ngPackage = RepoPath.Combine(directory, "ng-package.json");
		if (tree.IsFile(ngPackage))
		{
			using var ng = await ReadJsonAsync(ngPackage, cancellationToken);
			if (ng?.RootElement is { ValueKind: JsonValueKind.Object } ngRoot && String(ngRoot, "dest") is { } destText)
				dest = RepoPath.Resolve(directory, destText) ?? throw new GraphException($"{ngPackage}: dest '{destText}' points outside the repository.");
		}

		// A workspace root's own package.json is a package only when it declares a name and is not private; it is never
		// the input of everything below it, or every change anywhere would release it.
		if (directory.Length == 0) inputs = ["package.json"];

		return new ProjectNode
		{
			Path = path,
			Kind = PackageKind.Npm,
			Id = name,
			Publishable = !isPrivate && name is not null,
			Dependencies = dependencies,
			Inputs = inputs,
			DeclaredVersion = String(manifest, "version"),
			NgPackageDest = dest,
			FrameworkPackage = MajorAnchor.FrameworkPackages.FirstOrDefault(f => dependencies.Any(d => d.Name == f && d.Kind != DependencyKind.Dev)),
		};
	}

	public async Task<NpmLockfile?> ReadLockfileAsync(CancellationToken cancellationToken)
	{
		if (await tree.ReadAsync("package-lock.json", Limits.MaxLockfileBytes, cancellationToken) is not { } bytes) return null;
		try { return NpmLockfile.Parse(bytes); }
		catch (JsonException e) { warnings.Add($"package-lock.json: not readable JSON ({e.Message})."); return null; }
	}

	internal static bool IsUnderNodeModules(string path)
		=> path.StartsWith("node_modules/", StringComparison.Ordinal) || path.Contains("/node_modules/", StringComparison.Ordinal);

	private static string? String(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private async Task<JsonDocument?> ReadJsonAsync(string path, CancellationToken cancellationToken)
	{
		if (await tree.ReadAsync(path, Limits.MaxManifestBytes, cancellationToken) is not { } bytes) return null;
		var json = bytes.AsMemory();
		if (json.Span.StartsWith("﻿"u8)) json = json[3..];
		try { return JsonDocument.Parse(json, Options); }
		catch (JsonException e) { warnings.Add($"{path}: not readable JSON ({e.Message})."); return null; }
	}
}
