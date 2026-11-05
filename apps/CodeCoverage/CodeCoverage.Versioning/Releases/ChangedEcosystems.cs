using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.Versioning.Releases;

/// <summary>
/// The ecosystems a change touches (#570 M8b, PRD D5): those of the packages whose inputs it changes, by the test the
/// planner uses to decide a package releases (ignored globs and test projects do not count). A pull request that only
/// changes <c>libs/node_packages/**</c> therefore touches no <c>dotnet</c> line.
/// </summary>
public static class ChangedEcosystems
{
	public static async Task<IReadOnlyList<string>> OfAsync(ProjectGraph graph, IInputDiff diff, string from, string to, VersioningSettings settings,
		CancellationToken cancellationToken = default)
	{
		var found = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var package in graph.Packages.OrderBy(p => p.Id, StringComparer.Ordinal))
		{
			if (Ecosystems.Of(graph, package, settings.LinePins) is not { } ecosystem || found.Contains(ecosystem)) continue;
			if ((await ReleasePlanner.ChangedInputsAsync(graph, settings, diff, package, from, to, cancellationToken)).Count > 0)
				found.Add(ecosystem);
		}
		return [.. found];
	}
}
