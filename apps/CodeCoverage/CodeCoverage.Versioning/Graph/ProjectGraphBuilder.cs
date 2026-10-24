namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// Builds the <see cref="ProjectGraph"/> of a commit from its tree alone (PRD D5): every <c>.csproj</c> and every npm
/// package, never below <c>node_modules</c>, never through a symbolic link, within the project and edge caps.
/// </summary>
public static class ProjectGraphBuilder
{
	public static async Task<ProjectGraph> BuildAsync(IRepositoryTree tree, CancellationToken cancellationToken = default)
	{
		var warnings = new List<string>();
		var projects = new List<ProjectNode>();

		var dotnet = new DotnetProjectReader(tree, warnings);
		foreach (var (path, entry) in tree.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
		{
			if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || NpmProjectReader.IsUnderNodeModules(path)) continue;
			if (entry.IsSymlink) { warnings.Add($"{path}: a symbolic link, not read."); continue; }
			if (!entry.IsFile) continue;
			if (await dotnet.ReadAsync(path, cancellationToken) is { } project) Add(project);
		}

		var npm = new NpmProjectReader(tree, warnings);
		foreach (var path in await npm.DiscoverAsync(cancellationToken))
			if (await npm.ReadAsync(path, cancellationToken) is { } project) Add(project);
		var lockfile = await npm.ReadLockfileAsync(cancellationToken);

		var graph = new ProjectGraph(projects, lockfile, warnings);
		var edges = projects.Sum(p => graph.InternalDependencies(p).Count());
		if (edges > Limits.MaxEdges) throw new GraphException($"The repository has more than {Limits.MaxEdges} edges between its projects.");

		var duplicates = graph.Packages.GroupBy(p => (p.Kind, p.Id)).Where(g => g.Count() > 1).ToList();
		foreach (var duplicate in duplicates)
			throw new GraphException($"{duplicate.Key.Id} is declared by more than one project: {string.Join(", ", duplicate.Select(p => p.Path))}.");
		return graph;

		void Add(ProjectNode project)
		{
			projects.Add(project);
			if (projects.Count > Limits.MaxProjects) throw new GraphException($"The repository has more than {Limits.MaxProjects} projects.");
		}
	}
}
