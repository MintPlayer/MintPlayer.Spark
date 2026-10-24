namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// Builds the <see cref="ProjectGraph"/> of a commit from its tree alone (PRD D5): every <c>.csproj</c> and every npm
/// package, never below <c>node_modules</c>, never through a symbolic link, within the project and edge caps.
/// </summary>
public static class ProjectGraphBuilder
{
	/// <param name="ignore">
	/// <c>versioning.ignore</c>: a project file matching one of these globs is not part of the graph, so a repository keeps
	/// test fixtures that copy its real packages (<c>**/fixtures/**</c>) out of it. They count as unchanged inputs anyway.
	/// </param>
	public static async Task<ProjectGraph> BuildAsync(IRepositoryTree tree, IReadOnlyList<string>? ignore = null, CancellationToken cancellationToken = default)
	{
		var warnings = new List<string>();
		var projects = new List<ProjectNode>();
		var ignored = (ignore ?? []).Select(g => new PathGlob(g)).ToList();
		bool Ignored(string path) => ignored.Any(g => g.Matches(path));

		var dotnet = new DotnetProjectReader(tree, warnings);
		foreach (var (path, entry) in tree.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
		{
			if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || NpmProjectReader.IsUnderNodeModules(path) || Ignored(path)) continue;
			if (entry.IsSymlink) { warnings.Add($"{path}: a symbolic link, not read."); continue; }
			if (!entry.IsFile) continue;
			if (await dotnet.ReadAsync(path, cancellationToken) is { } project) Add(project);
		}

		var npm = new NpmProjectReader(tree, warnings);
		foreach (var path in await npm.DiscoverAsync(cancellationToken))
			if (!Ignored(path) && await npm.ReadAsync(path, cancellationToken) is { } project) Add(project);
		var lockfile = await npm.ReadLockfileAsync(cancellationToken);

		// An id two projects declare (test fixtures copy real-looking projects: this repository holds three "App"s) names no
		// package: none of them is versioned, and the warning names the paths for a versioning.ignore glob. Should one of them be the real package, the build packs it and the run's
		// packed-package cross-check fails loudly (ReleaseRun), so it is never skipped in silence.
		var duplicates = projects.Where(p => p.Publishable && p.Id is not null).GroupBy(p => (p.Kind, p.Id)).Where(g => g.Count() > 1).ToList();
		foreach (var duplicate in duplicates)
			warnings.Add($"{duplicate.Key.Id} is declared by more than one project ({string.Join(", ", duplicate.Select(p => p.Path))}), so none of them is versioned.");
		if (duplicates.Count > 0)
		{
			var ambiguous = duplicates.SelectMany(g => g).ToHashSet();
			projects = [.. projects.Select(p => ambiguous.Contains(p) ? p with { Publishable = false } : p)];
		}

		var graph = new ProjectGraph(projects, lockfile, warnings);
		var edges = projects.Sum(p => graph.InternalDependencies(p).Count());
		if (edges > Limits.MaxEdges) throw new GraphException($"The repository has more than {Limits.MaxEdges} edges between its projects.");
		return graph;

		void Add(ProjectNode project)
		{
			projects.Add(project);
			if (projects.Count > Limits.MaxProjects) throw new GraphException($"The repository has more than {Limits.MaxProjects} projects.");
		}
	}
}
