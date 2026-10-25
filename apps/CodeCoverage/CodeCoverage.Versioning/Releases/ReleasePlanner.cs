using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>A <c>versioning.groups</c> entry (D13): members release together at the group's highest version.</summary>
public sealed record LockstepGroup(string Name, IReadOnlyList<string> Packages)
{
	/// <summary>Whether <paramref name="packageId"/> is a member; <c>*</c> in a pattern matches any characters.</summary>
	public bool Contains(string packageId) => Packages.Any(p => PathGlob.Segment(p, packageId));
}

/// <summary>The <c>versioning</c> block of <c>coverage.yml</c>, as far as the release set needs it.</summary>
public sealed record VersioningSettings
{
	public static IReadOnlyList<string> DefaultIgnore { get; } = ["**/*.md"];

	/// <summary>Globs of changed files that never release a package (D4); test projects are always ignored.</summary>
	public IReadOnlyList<string> Ignore { get; init; } = DefaultIgnore;

	public IReadOnlyList<LockstepGroup> Groups { get; init; } = [];

	/// <summary><c>versioning.packages.&lt;id&gt;.anchor</c>: a framework package id or <c>free</c> (D7).</summary>
	public IReadOnlyDictionary<string, string> Anchors { get; init; } = new Dictionary<string, string>();

	/// <summary><c>versioning.releaseBranches</c> (D10): the first pattern with one <c>*</c> names a cut branch (D12).</summary>
	public IReadOnlyList<string> ReleaseBranches { get; init; } = Releases.ReleaseBranches.Default;

	/// <summary><c>versioning.prerelease</c> (D8): the label every package releases with; null mirrors upstream.</summary>
	public PrereleaseLabel? Prerelease { get; init; }

	/// <summary><c>versioning.packages.&lt;id&gt;.prerelease</c>: a label, or null for <c>upstream</c>; wins over <see cref="Prerelease"/>.</summary>
	public IReadOnlyDictionary<string, PrereleaseLabel?> PrereleasePins { get; init; } = new Dictionary<string, PrereleaseLabel?>();
}

/// <summary>What the build knows about one packed package (D5): what static parsing cannot read.</summary>
public sealed record PackageBuildFacts
{
	/// <summary>The public API hash of this build (D6), <c>sha256:…</c>.</summary>
	public string? ApiHash { get; init; }

	/// <summary>The hash of the last tag's build when its annotation has none (S7).</summary>
	public string? PreviousApiHash { get; init; }

	public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

	/// <summary>The resolved versions of the package's dependencies, by name: what restore actually picked.</summary>
	public IReadOnlyDictionary<string, string> ResolvedVersions { get; init; } = new Dictionary<string, string>();
}

public sealed record ReleaseContext
{
	public required ProjectGraph Graph { get; init; }

	/// <summary>The commit being released.</summary>
	public required string Head { get; init; }

	public required TagHistory Tags { get; init; }

	public required IInputDiff Diff { get; init; }

	public VersioningSettings Settings { get; init; } = new();

	public IReadOnlyDictionary<string, PackageBuildFacts> Build { get; init; } = new Dictionary<string, PackageBuildFacts>();

	/// <summary>The .NET SDK version the build ran on (<c>dotnet --version</c>), null when unknown: the platform's maturity (D8).</summary>
	public string? Sdk { get; init; }

	public bool OnDefaultBranch { get; init; } = true;

	/// <summary>The major of the release branch being built (D10); null on the default branch.</summary>
	public int? BranchMajor { get; init; }
}

/// <param name="Since">The commit of the previous tag on the package's line; null for a first release (the notes, D15).</param>
/// <param name="Inputs">The paths whose commits the notes list: the package's inputs and those of the projects compiled into it.</param>
/// <param name="Follows">The released dependencies this release follows (D4), for the notes' "Dependency update" lines.</param>
/// <param name="MovedFromMajor">
/// On the default branch, the major a platform-anchored package leaves behind when its platform's major moves (D12):
/// the line that gets a release branch. Null for a free package, which never cuts one.
/// </param>
public sealed record PlannedRelease(ProjectNode Project, PackageVersion Version, IReadOnlyList<string> Reasons, string? ApiHash, bool TaggedByHand,
	string? Since = null, IReadOnlyList<string>? Inputs = null, IReadOnlyList<VersionTag>? Follows = null, int? MovedFromMajor = null)
{
	public string Id => Project.Id!;

	public VersionTag Tag => VersionTag.For(Id, Version);
}

public sealed record PackageNote(string Id, string Reason);

public sealed record ReleasePlan(IReadOnlyList<PlannedRelease> Releases, IReadOnlyList<PackageNote> Skipped, IReadOnlyList<PackageNote> Refused)
{
	public PlannedRelease? Of(string id) => Releases.FirstOrDefault(r => r.Id == id);
}

/// <summary>
/// The release set of a run (D4): which packages get a tag, why, and at which version. A package releases when its
/// inputs changed since its last tag on the line, when an in-repo dependency it must follow releases, when a lockstep
/// group member releases (D13), or when a person tagged it on HEAD (D3).
/// </summary>
public static class ReleasePlanner
{
	public static async Task<ReleasePlan> PlanAsync(ReleaseContext context, CancellationToken cancellationToken = default)
	{
		var order = TopologicalOrder(context.Graph);
		var forced = new Dictionary<string, string>(StringComparer.Ordinal);
		var groupVersions = new Dictionary<string, PackageVersion>(StringComparer.Ordinal);
		var refusedGroups = new Dictionary<string, string>(StringComparer.Ordinal);
		var changes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

		// Groups feed back into dependents (a member released by its group can move a dependent's range), so the plan is
		// recomputed until it stops moving; every round only adds members or raises versions, so it ends.
		for (var round = 0; round <= order.Count + 1; round++)
		{
			var plan = await PassAsync(context, order, forced, groupVersions, refusedGroups, changes, cancellationToken);
			var moved = false;
			foreach (var group in context.Settings.Groups)
			{
				var members = order.Where(p => group.Contains(p.Id!)).ToList();
				var released = plan.Releases.Where(r => group.Contains(r.Id)).ToList();
				if (released.Count == 0 || refusedGroups.ContainsKey(group.Name)) continue;
				if (released.Select(r => r.Version.Major).Distinct().Count() > 1)
				{
					refusedGroups[group.Name] = $"Lockstep group {group.Name} computes more than one major ({string.Join(", ", released.Select(r => $"{r.Id} {r.Version}"))}).";
					moved = true;
					continue;
				}
				var highest = released.Max(r => r.Version)!;
				foreach (var member in members)
				{
					if (!forced.ContainsKey(member.Id!) && plan.Of(member.Id!) is null) { forced[member.Id!] = $"lockstep group {group.Name} releases"; moved = true; }
					if (plan.Of(member.Id!) is { TaggedByHand: false } own && own.Version < highest && groupVersions.GetValueOrDefault(member.Id!) != highest)
					{
						groupVersions[member.Id!] = highest;
						moved = true;
					}
				}
			}
			if (!moved) return plan;
		}
		throw new VersioningException("The release set did not settle; a lockstep group and its dependents keep moving each other.");
	}

	private static async Task<ReleasePlan> PassAsync(ReleaseContext context, IReadOnlyList<ProjectNode> order, Dictionary<string, string> forced,
		Dictionary<string, PackageVersion> groupVersions, Dictionary<string, string> refusedGroups, Dictionary<string, IReadOnlyList<string>> changes,
		CancellationToken cancellationToken)
	{
		var graph = context.Graph;
		var released = new Dictionary<string, PlannedRelease>(StringComparer.Ordinal);
		var skipped = new List<PackageNote>();
		var refused = new List<PackageNote>();
		var byHand = context.Tags.On(context.Head).ToDictionary(t => t.Tag.PackageId, StringComparer.Ordinal);

		foreach (var project in order)
		{
			var id = project.Id!;
			var history = context.Tags.Of(id);
			var facts = context.Build.GetValueOrDefault(id);

			if (context.Settings.Groups.FirstOrDefault(g => g.Contains(id) && refusedGroups.ContainsKey(g.Name)) is { } badGroup)
			{
				refused.Add(new PackageNote(id, refusedGroups[badGroup.Name]));
				continue;
			}
			if (byHand.TryGetValue(id, out var tagged))
			{
				released[id] = tagged.Annotation?.EngineVersion is null
					? new PlannedRelease(project, tagged.Version, [$"{tagged.Tag.Name} was tagged by hand on this commit"], facts?.ApiHash, TaggedByHand: true)
					: Rerun(context, project, facts, history, tagged);
				continue;
			}

			MajorAnchor anchor;
			try { anchor = AnchorOf(context, project, facts); }
			catch (VersioningException e) { refused.Add(new PackageNote(id, e.Message)); continue; }

			var lineMajor = anchor.Major ?? context.BranchMajor ?? history.LastOrDefault()?.Version.Major ?? 1;
			var lastOnLine = history.LastOrDefault(t => t.Version.Major == lineMajor);
			var reasons = new List<string>();

			if (lastOnLine is null)
				reasons.Add(history.Count == 0 ? "first release" : $"first release on the {lineMajor}.x line");
			else
			{
				var key = id + "@" + lastOnLine.Commit;
				if (!changes.TryGetValue(key, out var changed))
					changes[key] = changed = await ChangedInputsAsync(context, project, lastOnLine.Commit, cancellationToken);
				if (changed.Count > 0)
					reasons.Add($"inputs changed since {lastOnLine.Tag.Name}: {string.Join(", ", changed.Take(3))}{(changed.Count > 3 ? $" and {changed.Count - 3} more" : "")}");
			}

			var follows = new List<VersionTag>();
			foreach (var (dependency, declared) in graph.InternalDependencies(project))
				if (dependency.Id is { } dependencyId && released.TryGetValue(dependencyId, out var dependencyRelease)
					&& FollowReason(context, project, declared, dependencyRelease) is { } reason)
				{
					reasons.Add(reason);
					follows.Add(dependencyRelease.Tag);
				}

			if (forced.TryGetValue(id, out var forcedReason)) reasons.Add(forcedReason);
			if (reasons.Count == 0) continue;

			var lastApiHash = lastOnLine?.Annotation?.ApiHash ?? facts?.PreviousApiHash;
			var apiChanged = !(lastApiHash is not null && facts?.ApiHash is not null && lastApiHash == facts.ApiHash);
			var decision = VersionCalculator.Next(new VersionRequest(
				anchor,
				context.BranchMajor is null ? history.LastOrDefault()?.Version : lastOnLine?.Version,
				history.Select(t => t.Version).ToList(),
				apiChanged,
				ReleaseLabel(context, project, anchor, facts, released),
				context.OnDefaultBranch,
				context.BranchMajor));

			switch (decision)
			{
				case VersionDecision.Release(var version):
					if (groupVersions.TryGetValue(id, out var groupVersion) && groupVersion > version)
					{
						if (history.Any(t => t.Version == groupVersion)) { refused.Add(new PackageNote(id, $"Lockstep version {groupVersion} already exists for {id}.")); break; }
						version = groupVersion;
					}
					// The package's previous major is the major it computed at its last release, which stands for M(first parent).
					var movedFrom = context.OnDefaultBranch && !anchor.IsFree && history.LastOrDefault() is { } previous && version.Major > previous.Version.Major
						? previous.Version.Major
						: (int?)null;
					released[id] = new PlannedRelease(project, version, reasons, facts?.ApiHash, TaggedByHand: false,
						lastOnLine?.Commit, InputsOf(graph, project), follows, movedFrom);
					break;
				case VersionDecision.Skip(var why):
					skipped.Add(new PackageNote(id, why));
					break;
				case VersionDecision.Refuse(var why):
					refused.Add(new PackageNote(id, why));
					break;
			}
		}
		return new ReleasePlan(order.Where(p => released.ContainsKey(p.Id!)).Select(p => released[p.Id!]).ToList(), skipped, refused);
	}

	/// <summary>
	/// A tag the engine itself wrote on this commit (its annotation names the engine): an earlier run of the same commit
	/// that failed or was cancelled after tagging (#544 D3). The re-run releases the same version again, as the engine's
	/// own release, so the writer reports it already present and the notes, Releases and branch cut that run never got
	/// to are made now. A tag a person pushed carries no engine line and stays a fixed version (D3 of #153).
	/// </summary>
	private static PlannedRelease Rerun(ReleaseContext context, ProjectNode project, PackageBuildFacts? facts, IReadOnlyList<TaggedVersion> history, TaggedVersion tagged)
	{
		var previous = history.LastOrDefault(t => t.Version < tagged.Version);
		var sinceOnLine = history.LastOrDefault(t => t.Version < tagged.Version && t.Version.Major == tagged.Version.Major);
		int? movedFrom = null;
		try
		{
			if (context.OnDefaultBranch && !AnchorOf(context, project, facts).IsFree && previous is not null && tagged.Version.Major > previous.Version.Major)
				movedFrom = previous.Version.Major;
		}
		catch (VersioningException)
		{
			// The anchor could not be read again; the release stands, only its branch cut is left out.
		}
		return new PlannedRelease(project, tagged.Version, [$"{tagged.Tag.Name} was released by an earlier run of this commit"],
			tagged.Annotation!.ApiHash ?? facts?.ApiHash, TaggedByHand: false, sinceOnLine?.Commit, InputsOf(context.Graph, project), MovedFromMajor: movedFrom);
	}

	/// <summary>
	/// Whether a dependent must follow its released dependency (D4). .NET always follows: the new build raises its
	/// <c>&gt;=</c> floor. npm follows only when the declared range stops admitting the new version, or a peer's major
	/// moves; otherwise <c>npm update</c> already picks it up.
	/// </summary>
	private static string? FollowReason(ReleaseContext context, ProjectNode project, PackageDependency? declared, PlannedRelease dependency)
	{
		if (project.Kind == PackageKind.NuGet || declared is null) return $"{dependency.Id} {dependency.Version} is released";
		var version = dependency.Version.ToString();
		if (declared.Kind == DependencyKind.Peer)
		{
			var previousMajor = context.Tags.Of(dependency.Id).Where(t => t.Version < dependency.Version).Select(t => (int?)t.Version.Major).LastOrDefault();
			if (previousMajor is { } major && major != dependency.Version.Major) return $"peer {dependency.Id} moves to major {dependency.Version.Major}";
		}
		if (declared.Range is null || !NpmRange.TryParse(declared.Range, out var range))
			return $"{dependency.Id} {version} is released and '{declared.Range}' is not a range the engine reads";
		return range.Admits(version) ? null : $"{dependency.Id} {version} is outside '{declared.Range}'";
	}

	private static MajorAnchor AnchorOf(ReleaseContext context, ProjectNode project, PackageBuildFacts? facts)
	{
		if (context.Settings.Anchors.TryGetValue(project.Id!, out var setting))
		{
			var resolved = string.Equals(setting, "free", StringComparison.OrdinalIgnoreCase) ? null
				: project.Kind == PackageKind.Npm ? context.Graph.Lockfile?.Resolve(setting, project.Directory)
				: facts?.ResolvedVersions.GetValueOrDefault(setting);
			return MajorAnchor.FromOverride(setting, resolved);
		}
		return context.Graph.AnchorOf(project, facts?.TargetFrameworks);
	}

	/// <summary>
	/// The label of the release (D8): a pin from <c>versioning.packages.&lt;id&gt;.prerelease</c> or
	/// <c>versioning.prerelease</c> when there is one, which upstream never overrides; else <see cref="UpstreamLabel"/>.
	/// </summary>
	private static PrereleaseLabel? ReleaseLabel(ReleaseContext context, ProjectNode project, MajorAnchor anchor, PackageBuildFacts? facts,
		Dictionary<string, PlannedRelease> released)
		=> context.Settings.PrereleasePins.TryGetValue(project.Id!, out var pin)
			? pin ?? UpstreamLabel(context, project, anchor, facts, released)
			: context.Settings.Prerelease ?? UpstreamLabel(context, project, anchor, facts, released);

	/// <summary>
	/// The least mature prerelease among what the package ships against (D8): its runtime and peer dependencies as
	/// resolved, its in-repo dependencies at the version this run gives them or their last tag, and for a .NET package
	/// anchored on its target framework, the SDK the build ran on when that is the same major (net11.0 built on
	/// 11.0.100-rc.2 targets a platform still in rc).
	/// </summary>
	private static PrereleaseLabel? UpstreamLabel(ReleaseContext context, ProjectNode project, MajorAnchor anchor, PackageBuildFacts? facts,
		Dictionary<string, PlannedRelease> released)
	{
		var graph = context.Graph;
		var versions = new List<string>();
		if (project.Kind == PackageKind.NuGet && !context.Settings.Anchors.ContainsKey(project.Id!) && anchor.Major is { } major
			&& context.Sdk is { } sdk && SemVer.TryParse(sdk, out var sdkVersion) && sdkVersion.Major == major)
			versions.Add(sdk);
		var internalIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (dependency, _) in graph.InternalDependencies(project))
		{
			if (dependency.Id is not { } dependencyId || !dependency.Publishable) continue;
			internalIds.Add(dependencyId);
			var version = released.TryGetValue(dependencyId, out var release) ? release.Version : context.Tags.Of(dependencyId).LastOrDefault()?.Version;
			if (version is not null) versions.Add(version.ToString());
		}
		foreach (var dependency in project.Dependencies)
		{
			if (dependency.Kind == DependencyKind.Dev || internalIds.Contains(dependency.Name)) continue;
			var resolved = facts?.ResolvedVersions.GetValueOrDefault(dependency.Name)
				?? (project.Kind == PackageKind.Npm ? graph.Lockfile?.Resolve(dependency.Name, project.Directory) : dependency.Range);
			if (resolved is not null) versions.Add(resolved);
		}
		return Labels.LeastMature(versions);
	}

	/// <summary>
	/// The changed files among the package's inputs since <paramref name="since"/>, without the ignored ones: the
	/// <c>versioning.ignore</c> globs, and test projects. Non-packable projects it references are compiled into it, so
	/// their inputs count as its own.
	/// </summary>
	private static async Task<IReadOnlyList<string>> ChangedInputsAsync(ReleaseContext context, ProjectNode project, string since, CancellationToken cancellationToken)
	{
		var graph = context.Graph;
		var ignore = context.Settings.Ignore.Select(g => new PathGlob(g)).ToList();
		var tests = graph.Projects.Where(IsTestProject).Select(p => p.Directory).Where(d => d.Length > 0 && d != project.Directory).ToList();
		var changed = await context.Diff.ChangedAsync(since, context.Head, InputsOf(graph, project), cancellationToken);
		return changed.Where(path => !ignore.Any(g => g.Matches(path)) && !tests.Any(d => RepoPath.IsUnder(path, d))).ToList();
	}

	/// <summary>The package's inputs, with those of the non-packable projects it references: they are compiled into it.</summary>
	private static IReadOnlyList<string> InputsOf(ProjectGraph graph, ProjectNode project)
	{
		var inputs = new SortedSet<string>(StringComparer.Ordinal);
		var visited = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<ProjectNode>([project]);
		while (pending.TryPop(out var current))
		{
			if (!visited.Add(current.Path)) continue;
			inputs.UnionWith(current.Inputs);
			foreach (var path in current.ProjectReferences)
				if (graph.ByPath(path) is { Publishable: false } embedded) pending.Push(embedded);
		}
		return inputs.ToList();
	}

	private static bool IsTestProject(ProjectNode project)
		=> project.Kind == PackageKind.NuGet && project.Dependencies.Any(d => d.Name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

	/// <summary>The packages, dependencies first. A cycle between packages cannot be released and fails the run.</summary>
	internal static IReadOnlyList<ProjectNode> TopologicalOrder(ProjectGraph graph)
	{
		var packages = graph.Packages.ToList();
		var order = new List<ProjectNode>();
		var state = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var package in packages.OrderBy(p => p.Id, StringComparer.Ordinal)) Visit(package, []);
		return order;

		void Visit(ProjectNode node, List<string> trail)
		{
			if (state.TryGetValue(node.Path, out var mark))
			{
				if (mark == 1) throw new GraphException($"The packages depend on each other in a cycle: {string.Join(" -> ", trail.Append(node.Id ?? node.Path))}.");
				return;
			}
			state[node.Path] = 1;
			trail.Add(node.Id ?? node.Path);
			foreach (var (dependency, _) in graph.InternalDependencies(node)) Visit(dependency, trail);
			trail.RemoveAt(trail.Count - 1);
			state[node.Path] = 2;
			if (node.Publishable && node.Id is not null) order.Add(node);
		}
	}
}
