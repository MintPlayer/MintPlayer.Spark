namespace CodeCoverage.Versioning.Graph;

/// <summary>The caps of PRD D5. A repository over one of them fails the run with the limit named, never a partial graph.</summary>
public static class Limits
{
	public const int MaxTreeEntries = 500_000;
	public const int MaxProjects = 2_000;
	public const int MaxEdges = 50_000;
	public const int MaxPackagesPerRun = 500;

	/// <summary>A project file, props file, package.json, project.json or ng-package.json.</summary>
	public const long MaxManifestBytes = 2 * 1024 * 1024;

	/// <summary><c>package-lock.json</c>: large monorepos reach tens of megabytes.</summary>
	public const long MaxLockfileBytes = 64 * 1024 * 1024;

	/// <summary>How many <c>Directory.Build.props</c> files one project may chain through.</summary>
	public const int MaxPropsChain = 16;

	public const int MaxGlobLength = 256;
	public const int MaxGlobSegments = 32;
}
