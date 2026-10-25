using System.Text.Json;
using CodeCoverage.Versioning.Api;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Build;

/// <param name="WorkingDirectory">The checkout the workflow built in.</param>
/// <param name="Commit">The commit it built: HEAD of the checkout.</param>
/// <param name="Configuration">The build configuration whose <c>bin</c> output is hashed.</param>
/// <param name="BranchMajor">The release branch's major, null on the default branch: picks the line whose last tag's hash is looked up.</param>
/// <param name="Registry">Where a previous hash comes from when the last tag has none (S7); null to never ask.</param>
/// <param name="Ignore"><c>versioning.ignore</c>, which keeps matching projects out of the graph as it does in the run.</param>
/// <param name="Sdk">The .NET SDK the build ran on (<see cref="DotnetSdk"/>), copied into the manifest when a .NET package is in it.</param>
public sealed record BuildManifestRequest(string WorkingDirectory, string Commit, string Configuration, int? BranchMajor, IPackageRegistry? Registry,
	IReadOnlyList<string>? Ignore = null, string? Sdk = null);

/// <summary>A manifest and what the action should tell the workflow about how it was collected.</summary>
public sealed record BuildManifestResult(ReleaseManifest Manifest, IReadOnlyList<string> Notes);

/// <summary>
/// The action's half of a run (PRD D5, D6), the same in both modes: after the build and before pack, read what only the
/// build knows for every publishable package of the commit's statically parsed graph. That is the API hash of its built
/// output, the target frameworks it was built for, and what restore resolved. A package with build output counts as
/// packed. Nothing the build produced is loaded or executed: assemblies are read as metadata, declarations as text.
/// </summary>
public static class BuildManifest
{
	public const long MaxDeclarationBytes = 4 * 1024 * 1024;
	public const int MaxDeclarationFiles = 5_000;
	public const long MaxAssetsFileBytes = 64 * 1024 * 1024;

	public static async Task<BuildManifestResult> CollectAsync(GitCli git, BuildManifestRequest request, CancellationToken cancellationToken = default)
	{
		var graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, request.Commit, cancellationToken), request.Ignore, cancellationToken);
		var tags = await TagHistory.ReadAsync(git, cancellationToken);
		var notes = new List<string>(graph.Warnings);
		var packages = new List<ManifestPackage>();

		foreach (var node in graph.Packages.OrderBy(p => p.Id, StringComparer.Ordinal))
		{
			var package = node.Kind == PackageKind.NuGet ? DotnetPackage(request, node, notes) : NpmPackage(request, node, notes);

			// The planner compares against the last tag on the run's line; only a tag without an annotation needs the registry.
			var history = tags.Of(node.Id!);
			var line = request.BranchMajor ?? history.LastOrDefault()?.Version.Major;
			if (request.Registry is not null && package.ApiHash is not null
				// A lightweight tag (one made by hand) has no annotation at all; `{ Annotation.ApiHash: null }` would skip it.
				&& history.LastOrDefault(t => t.Version.Major == line) is { } last && last.Annotation?.ApiHash is null)
			{
				try
				{
					package = package with { PreviousApiHash = await request.Registry.ApiHashAsync(node, last.Version.ToString(), cancellationToken) };
					if (package.PreviousApiHash is null) notes.Add($"{node.Id} {last.Version} is not on the public registry; its API counts as changed.");
				}
				catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
				{
					notes.Add($"{node.Id} {last.Version} could not be read from the registry ({e.GetType().Name}); its API counts as changed.");
				}
			}
			packages.Add(package);
		}
		var sdk = packages.Any(p => p.Kind == "nuget") ? request.Sdk : null;
		return new BuildManifestResult(new ReleaseManifest { Commit = request.Commit, Packages = packages, Sdk = sdk }, notes);
	}

	private static ManifestPackage DotnetPackage(BuildManifestRequest request, ProjectNode node, List<string> notes)
	{
		var projectDirectory = Local(request.WorkingDirectory, node.Directory);
		var assembly = (node.AssemblyName ?? node.Id!) + ".dll";
		var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (tfm, directory) in OutputDirectories(request, node, projectDirectory))
			if (Identifiers.IsTargetFramework(tfm) && File.Exists(Path.Combine(directory, assembly))) outputs.TryAdd(tfm, Path.Combine(directory, assembly));

		if (outputs.Count == 0)
		{
			notes.Add($"{node.Id}: no {assembly} under bin/{request.Configuration}; built with another configuration or output path? Its API counts as changed.");
			return new ManifestPackage { Id = node.Id, Kind = "nuget" };
		}

		var lines = new List<string>();
		foreach (var (tfm, path) in outputs)
		{
			using var stream = File.OpenRead(path);
			lines.AddRange(DotnetApiListing.List(stream).Select(l => $"{tfm} {l}"));
		}
		return new ManifestPackage
		{
			Id = node.Id,
			Kind = "nuget",
			ApiHash = DotnetApiListing.Hash(lines),
			TargetFrameworks = [.. outputs.Keys],
			ResolvedVersions = ResolvedVersions(request, node, projectDirectory),
			Packed = true,
		};
	}

	/// <summary>The default output layout and the artifacts layout (<c>UseArtifactsOutput</c>), per target framework.</summary>
	private static IEnumerable<(string Tfm, string Directory)> OutputDirectories(BuildManifestRequest request, ProjectNode node, string projectDirectory)
	{
		var bin = Path.Combine(projectDirectory, "bin", request.Configuration);
		IEnumerable<string> tfms = node.TargetFrameworks.Count > 0 ? node.TargetFrameworks
			: Directory.Exists(bin) ? Directory.EnumerateDirectories(bin).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal) : [];
		var projectName = Path.GetFileNameWithoutExtension(node.Path);
		var artifacts = Path.Combine(request.WorkingDirectory, "artifacts", "bin", projectName);
		var configuration = request.Configuration.ToLowerInvariant();
		foreach (var tfm in tfms)
		{
			yield return (tfm, Path.Combine(bin, tfm));
			yield return (tfm, Path.Combine(artifacts, $"{configuration}_{tfm.ToLowerInvariant()}"));
			if (node.TargetFrameworks.Count == 1) yield return (tfm, Path.Combine(artifacts, configuration));
		}
	}

	/// <summary>What restore resolved (<c>obj/project.assets.json</c>): the upstream versions D7 overrides and D8 read.</summary>
	private static IReadOnlyDictionary<string, string>? ResolvedVersions(BuildManifestRequest request, ProjectNode node, string projectDirectory)
	{
		var candidates = new[]
		{
			Path.Combine(projectDirectory, "obj", "project.assets.json"),
			Path.Combine(request.WorkingDirectory, "artifacts", "obj", Path.GetFileNameWithoutExtension(node.Path), "project.assets.json"),
		};
		var path = candidates.FirstOrDefault(File.Exists);
		if (path is null || new FileInfo(path).Length > MaxAssetsFileBytes) return null;

		using var stream = File.OpenRead(path);
		using var document = JsonDocument.Parse(stream);
		if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object) return null;
		var resolved = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var library in libraries.EnumerateObject())
		{
			if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
			var slash = library.Name.IndexOf('/');
			if (slash <= 0) continue;
			var (id, version) = (library.Name[..slash], library.Name[(slash + 1)..]);
			if (Identifiers.IsPackageId(id) && version.Length is > 0 and <= ReleaseManifest.MaxVersionLength) resolved[id] = version;
		}
		if (resolved.Count > ReleaseManifest.MaxResolvedVersions)
		{
			// Too many to send: keep what the package declares itself, which is what the planner reads first.
			var direct = node.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			return resolved.Where(r => direct.Contains(r.Key)).Take(ReleaseManifest.MaxResolvedVersions).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
		}
		return resolved;
	}

	private static ManifestPackage NpmPackage(BuildManifestRequest request, ProjectNode node, List<string> notes)
	{
		// A library with a build output (ng-packagr, Nx) publishes that; any other package publishes its own folder.
		var root = Local(request.WorkingDirectory, node.BuildOutput ?? node.Directory);
		var files = Directory.Exists(root) ? Declarations(root).ToList() : [];
		if (files.Count == 0)
		{
			notes.Add(node.BuildOutput is not null
				? $"{node.Id}: no .d.ts under {node.BuildOutput}; run the library build before this step. Its API counts as changed."
				: $"{node.Id}: no .d.ts files; its API counts as changed.");
			return new ManifestPackage { Id = node.Id, Kind = "npm", Packed = Directory.Exists(root) && node.BuildOutput is not null };
		}
		return new ManifestPackage
		{
			Id = node.Id,
			Kind = "npm",
			ApiHash = TypeScriptApiListing.Hash(TypeScriptApiListing.List(files.Select(f => (f, File.ReadAllText(Path.Combine(root, f)))))),
			Packed = true,
		};
	}

	/// <summary>Declaration files under <paramref name="root"/> as relative <c>/</c> paths, never below <c>node_modules</c>.</summary>
	internal static IEnumerable<string> Declarations(string root)
	{
		var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
		return Directory.EnumerateFiles(root, "*.d.ts", options)
			.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
			.Where(f => !f.Split('/').Contains("node_modules") && new FileInfo(Path.Combine(root, f)).Length <= MaxDeclarationBytes)
			.Order(StringComparer.Ordinal)
			.Take(MaxDeclarationFiles);
	}

	private static string Local(string workingDirectory, string repoPath)
		=> repoPath.Length == 0 ? workingDirectory : Path.Combine(workingDirectory, repoPath.Replace('/', Path.DirectorySeparatorChar));
}
