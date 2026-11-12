using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>A version tag the history backfill places: on <see cref="Commit"/>, its earliest occurrence on <see cref="Branch"/>.</summary>
public sealed record BackfillTag(VersionTag Tag, string Commit, string Branch);

/// <summary>
/// #638 D9: a tag the App made (annotated, with the App's tagger) on <see cref="Current"/>, while the history declares its
/// version first at <see cref="Commit"/> on <see cref="Branch"/>. Moving it leases on <see cref="TagObject"/>.
/// </summary>
public sealed record BackfillCollision(VersionTag Tag, string TagObject, string Current, string Commit, string Branch);

/// <param name="Tags">The tags to write, ordered by name.</param>
/// <param name="Branches">The branches walked: the default branch and the release lines.</param>
/// <param name="Collisions">The App's own tags away from their version's earliest occurrence, ordered by name (D9).</param>
public sealed record BackfillPlan(IReadOnlyList<BackfillTag> Tags, IReadOnlyList<string> Branches, IReadOnlyList<string> Notes)
{
	public IReadOnlyList<BackfillCollision> Collisions { get; init; } = [];
}

/// <summary>
/// #638 D5-D7: git tags are the versions' only record (D4), so a repository that declared its versions by hand before
/// onboarding gets a tag for every version each package declared, at the earliest commit that declared it. The pack on a
/// release line reads the tags reachable from the line (<c>tag --merged HEAD</c>, MintPlayer.Versioning.MSBuild), so a
/// version of major N goes on the N.x line's history when that line exists.
/// </summary>
/// <remarks>
/// Walked: the default branch and every branch the line pattern matches, never a feature branch (D6), each along its
/// first parents, so a version a pull request's branch declared on the way is not taken for one that shipped. Only the
/// commits that change a project file are read, and each one's graph once (S3: 184 of Spark's 342 commits).
/// </remarks>
public static class HistoryBackfill
{
	/// <summary>The commits read at most: a repository past it is refused rather than half backfilled.</summary>
	public const int MaxCommits = 5000;

	private const int MaxNotes = 20;

	private static readonly string[] ProjectFiles = ["*.csproj", "*.fsproj", "*.vbproj", "*.props", "*.targets", "package.json", "*/package.json"];

	/// <summary>Plans the tags missing from <paramref name="git"/>'s history; reads only, writes nothing.</summary>
	/// <param name="appTagger">The App's tagger: an annotated tag it made away from its earliest occurrence is a collision (D9).</param>
	public static async Task<BackfillPlan> PlanAsync(GitCli git, string defaultBranch, VersioningSettings settings, Tagger? appTagger = null,
		CancellationToken cancellationToken = default)
	{
		var notes = new List<string>();
		var lines = new Dictionary<string, ReleaseLine>(StringComparer.Ordinal);
		foreach (var branch in (await git.RunAsync(["for-each-ref", "--format=%(refname:strip=2)", "refs/heads"], cancellationToken: cancellationToken))
			.EnsureSuccess("for-each-ref").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (branch != defaultBranch && settings.Lines is { } pattern && pattern.TryMatch(branch, out var line)) lines[branch] = line!;
		List<string> branches = [defaultBranch, .. lines.Keys.Order(StringComparer.Ordinal)];

		var existing = await TagsAsync(git, cancellationToken);
		var declared = new Dictionary<string, IReadOnlyList<Declaration>>(StringComparer.Ordinal);
		// Per (tag name), the earliest commit on each branch that declares it.
		var earliest = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
		var found = new Dictionary<string, (VersionTag Tag, string? Ecosystem)>(StringComparer.Ordinal);
		foreach (var branch in branches)
		{
			var commits = (await git.RunAsync(["log", "--first-parent", "--reverse", "--format=%H", "refs/heads/" + branch, "--", .. ProjectFiles],
				cancellationToken: cancellationToken)).EnsureSuccess("log").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			foreach (var commit in commits)
			{
				if (!declared.TryGetValue(commit, out var declarations))
				{
					if (declared.Count >= MaxCommits)
						throw new VersioningException($"More than {MaxCommits} commits change a project file; the history is not backfilled. Tag the versions by hand.");
					declarations = await DeclarationsAsync(git, commit, settings, notes, cancellationToken);
					declared[commit] = declarations;
				}
				foreach (var declaration in declarations)
				{
					var name = declaration.Tag.Name;
					found.TryAdd(name, (declaration.Tag, declaration.Ecosystem));
					var byBranch = earliest.TryGetValue(name, out var known) ? known : earliest[name] = new Dictionary<string, string>(StringComparer.Ordinal);
					byBranch.TryAdd(branch, commit);
				}
			}
		}

		var tags = new List<BackfillTag>();
		var collisions = new List<BackfillCollision>();
		foreach (var (name, (tag, ecosystem)) in found.OrderBy(f => f.Key, StringComparer.Ordinal))
		{
			var byBranch = earliest[name];
			// D7: the line of the version's major first, whose history holds the default branch's up to its cut; then the
			// default branch; then any line, which only a version no line of its major declared reaches.
			var own = lines.Where(l => l.Value.Ecosystem == ecosystem && l.Value.Major == tag.Version.Major && byBranch.ContainsKey(l.Key))
				.Select(l => l.Key).Order(StringComparer.Ordinal).FirstOrDefault();
			var branch = own ?? (byBranch.ContainsKey(defaultBranch) ? defaultBranch : branches.First(byBranch.ContainsKey));
			if (existing.TryGetValue(name, out var present))
			{
				// D9: only the App's own tags are ever offered for a move; a lightweight tag or another tagger's is a person's.
				if (appTagger is not null && present.Commit != byBranch[branch] && present.Tagger == appTagger.Name && present.Email == appTagger.Email)
					collisions.Add(new BackfillCollision(tag, present.Object, present.Commit, byBranch[branch], branch));
				continue;
			}
			if (own is null && branch != defaultBranch && notes.Count < MaxNotes)
				notes.Add($"{name} is declared only on {branch}, not on a line of its major, so it is tagged there.");
			tags.Add(new BackfillTag(tag, byBranch[branch], branch));
		}
		return new BackfillPlan(tags, branches, notes) { Collisions = collisions };
	}

	/// <param name="Object">The ref's value: the tag object of an annotated tag, the commit of a lightweight one.</param>
	/// <param name="Tagger">The annotated tag's tagger name; null for a lightweight tag.</param>
	private sealed record ExistingTag(string Object, string Commit, string? Tagger, string? Email);

	private sealed record Declaration(VersionTag Tag, string? Ecosystem);

	/// <summary>Each package's declared version at <paramref name="commit"/>; a commit whose graph cannot be read gives none, with a note.</summary>
	private static async Task<IReadOnlyList<Declaration>> DeclarationsAsync(GitCli git, string commit, VersioningSettings settings, List<string> notes,
		CancellationToken cancellationToken)
	{
		ProjectGraph graph;
		try
		{
			graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, commit, cancellationToken), settings.Ignore, cancellationToken);
		}
		catch (GraphException e)
		{
			if (notes.Count < MaxNotes) notes.Add($"The projects at {commit[..12]} could not be read ({e.Message}); its versions are not backfilled.");
			return [];
		}
		var declarations = new List<Declaration>();
		foreach (var package in graph.Packages)
			if (package.Id is { } id && PackageVersion.TryParse(package.DeclaredVersion, out var version))
				declarations.Add(new Declaration(VersionTag.For(id, version), Ecosystems.Of(graph, package, settings.LinePins)));
		return declarations;
	}

	private static async Task<Dictionary<string, ExistingTag>> TagsAsync(GitCli git, CancellationToken cancellationToken)
	{
		var tags = new Dictionary<string, ExistingTag>(StringComparer.Ordinal);
		var listing = (await git.RunAsync(["for-each-ref",
			"--format=%(refname:strip=2)%00%(objectname)%00%(objecttype)%00%(taggername)%00%(taggeremail)%00%(*objectname)", "refs/tags"],
			cancellationToken: cancellationToken)).EnsureSuccess("for-each-ref").Text;
		foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			if (line.TrimEnd('\r').Split('\0') is not [var name, var value, var type, var tagger, var email, var peeled]) continue;
			tags[name] = type == "tag"
				? new ExistingTag(value, peeled, tagger, email.Trim().TrimStart('<').TrimEnd('>'))
				: new ExistingTag(value, value, null, null);
		}
		return tags;
	}
}
