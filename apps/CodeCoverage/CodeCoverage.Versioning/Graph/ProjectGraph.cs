using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Graph;

public enum PackageKind
{
	NuGet,
	Npm,
}

public enum DependencyKind
{
	/// <summary>Ships with the package: a .NET <c>PackageReference</c>, npm <c>dependencies</c>/<c>optionalDependencies</c>.</summary>
	Runtime,

	/// <summary>npm <c>peerDependencies</c>.</summary>
	Peer,

	/// <summary>Build-only: <c>PrivateAssets="all"</c>, npm <c>devDependencies</c>. Never decides a prerelease (D8).</summary>
	Dev,
}

/// <summary>A declared dependency on a package by name; <see cref="Range"/> is the version or range as written.</summary>
public sealed record PackageDependency(string Name, string? Range, DependencyKind Kind);

/// <summary>
/// One project of the repository, as read statically at a commit (PRD D5): a <c>.csproj</c> or a <c>package.json</c>.
/// </summary>
public sealed record ProjectNode
{
	/// <summary>The project file: <c>src/Mapper/Mapper.csproj</c>, <c>libs/ng-spark/package.json</c>.</summary>
	public required string Path { get; init; }

	public string Directory => RepoPath.Directory(Path);

	public required PackageKind Kind { get; init; }

	/// <summary>The NuGet <c>PackageId</c> or npm <c>name</c>; null when it could not be read statically.</summary>
	public required string? Id { get; init; }

	/// <summary>Packed or published: <c>IsPackable</c> for .NET, not <c>"private": true</c> for npm.</summary>
	public required bool Publishable { get; init; }

	/// <summary>The evaluated target frameworks (.NET). Empty when they use a property the parser could not resolve.</summary>
	public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

	/// <summary>Other projects this one references, by project file path (.NET <c>ProjectReference</c>).</summary>
	public IReadOnlyList<string> ProjectReferences { get; init; } = [];

	public IReadOnlyList<PackageDependency> Dependencies { get; init; } = [];

	/// <summary>
	/// What the package is built from: files and directories (a directory stands for everything below it). The release
	/// set compares their tree shas between the last tag and the commit (D4).
	/// </summary>
	public IReadOnlyList<string> Inputs { get; init; } = [];

	/// <summary>
	/// The version as written: npm's <c>version</c>; for .NET the unconditional <c>PackageVersion</c>, else <c>Version</c>,
	/// else <c>VersionPrefix</c>-<c>VersionSuffix</c>, possibly holding an unresolved <c>$(...)</c>. <c>0.0.0-PLACEHOLDER</c>
	/// once onboarded (D17).
	/// </summary>
	public string? DeclaredVersion { get; init; }

	/// <summary>The <c>Directory.Build.props</c> files the project imports, root first (.NET).</summary>
	public IReadOnlyList<string> BuildProps { get; init; } = [];

	/// <summary>The built assembly's file name without <c>.dll</c> (.NET), which the action hashes (D6). Null when it uses a property.</summary>
	public string? AssemblyName { get; init; }

	/// <summary>An Angular library's build output folder from <c>ng-package.json</c>, written by the action (D9).</summary>
	public string? NgPackageDest { get; init; }

	/// <summary>The frontend framework whose resolved major anchors this npm package (D7), if any.</summary>
	public string? FrameworkPackage { get; init; }
}

/// <summary>The packages of a repository at one commit and the edges between them.</summary>
public sealed class ProjectGraph
{
	private readonly Dictionary<string, ProjectNode> byPath;

	public ProjectGraph(IReadOnlyList<ProjectNode> projects, NpmLockfile? lockfile, IReadOnlyList<string> warnings)
	{
		Projects = projects;
		Lockfile = lockfile;
		Warnings = warnings;
		byPath = projects.ToDictionary(p => p.Path, StringComparer.Ordinal);
	}

	public IReadOnlyList<ProjectNode> Projects { get; }

	public NpmLockfile? Lockfile { get; }

	/// <summary>What the parser skipped and why: shown in the run's check so a missing package is explainable.</summary>
	public IReadOnlyList<string> Warnings { get; }

	public IEnumerable<ProjectNode> Packages => Projects.Where(p => p.Publishable && p.Id is not null);

	public ProjectNode? ByPath(string path) => byPath.GetValueOrDefault(path);

	public ProjectNode? ById(string id) => Packages.FirstOrDefault(p => p.Id == id);

	/// <summary>
	/// The projects this one depends on inside the repository: .NET project references, and npm dependencies whose
	/// name is a package of the repository (any kind but <see cref="DependencyKind.Dev"/>).
	/// </summary>
	public IEnumerable<(ProjectNode Project, PackageDependency? Dependency)> InternalDependencies(ProjectNode project)
	{
		foreach (var path in project.ProjectReferences)
			if (ByPath(path) is { } referenced) yield return (referenced, null);
		if (project.Kind != PackageKind.Npm) yield break;
		foreach (var dependency in project.Dependencies)
			if (dependency.Kind != DependencyKind.Dev
				&& Projects.FirstOrDefault(p => p.Kind == PackageKind.Npm && p.Id == dependency.Name) is { } internalPackage)
				yield return (internalPackage, dependency);
	}

	/// <summary>
	/// A run's packed packages must all be in the graph (D5): a package the server cannot see would be released with
	/// inputs nobody compared, so the run fails instead.
	/// </summary>
	public void CrossCheck(IEnumerable<string> packedIds)
	{
		var missing = packedIds.Where(id => ById(id) is null).ToList();
		if (missing.Count > 0)
			throw new GraphException($"The build packed {string.Join(", ", missing)}, which the repository's project files do not declare as packable. "
				+ (Warnings.Count > 0 ? "Parser notes: " + string.Join("; ", Warnings) : ""));
	}

	/// <summary>The anchor of a package from its target frameworks or its framework's resolved version (D7).</summary>
	public MajorAnchor AnchorOf(ProjectNode project, IReadOnlyList<string>? manifestTargetFrameworks = null)
	{
		if (project.Kind == PackageKind.NuGet)
		{
			var tfms = project.TargetFrameworks.Count > 0 ? project.TargetFrameworks : manifestTargetFrameworks ?? [];
			if (tfms.Count == 0) throw new VersioningException($"The target frameworks of {project.Id} could not be read from {project.Path} and the build did not report them.");
			return MajorAnchor.FromTargetFrameworks(tfms);
		}
		if (project.FrameworkPackage is not { } framework) return MajorAnchor.Free("a plain node library");
		var resolved = Lockfile?.Resolve(framework, project.Directory)
			?? throw new VersioningException($"{framework} is not resolved in package-lock.json, so the major of {project.Id} cannot be read.");
		var peer = project.Dependencies.FirstOrDefault(d => d.Name == framework && d.Kind == DependencyKind.Peer)?.Range;
		return MajorAnchor.FromFramework(framework, resolved, peer);
	}
}
