using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Validation;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>
/// The <c>versioning:</c> section of <c>coverage.yml</c> (PRD D2, D3, D7, D10, D13, D17), as both hosts read it.
/// </summary>
/// <remarks>
/// <para>
/// The engine takes no YAML package (see its csproj), so each host turns the file into this object its own way: the server
/// with YamlDotNet, the action by handing the section to the CLI as JSON. Defaults and validation live only here, so the
/// two hosts cannot read one file two ways.
/// </para>
/// <para>
/// A repository is versioned when its default branch's <c>coverage.yml</c> has the section at all (D17), an empty one
/// included. Unlike <c>dependencies:</c>, an invalid section refuses the run rather than falling back to defaults: a
/// typo in a group or an anchor would otherwise publish a wrong version, which can never be taken back.
/// </para>
/// <code>
/// versioning:
///   mode: server                     # server | local (D2), default server
///   dispatch: publish.yml            # trigger B (D3): fired on a merge that changes a package
///   releaseBranches: ["release/*.x"] # D10, unused when lines are on
///   lines:                           # #568 D15: packages release only from release lines, never from the default branch
///     pattern: release/{ecosystem}-{major}.x # D2, the default
///   dependencyRanges: minimum        # #568 D10b: minimum (>= x, the default) or major-bounded ([x, nextMajor))
///   ignore: ["**/*.md"]              # D4: changed files that never release a package
///   prerelease: preview              # D8: every package releases with this label until the line is removed
///   groups:                          # D13
///     - name: mapper
///       packages: [MintPlayer.Mapper, MintPlayer.Mapper.*]
///   packages:
///     MintPlayer.SourceGenerators: { anchor: free }   # D7: free, or a framework package id
///     "@mintplayer/ng-spark": { prerelease: upstream } # D8: a label, stable, or upstream to take the label from what it ships against
///     Acme.Generators: { line: dotnet }  # #568 D19: the ecosystem whose newest line releases an unanchored package
/// </code>
/// </remarks>
public sealed class VersioningConfig
{
	public const string ServerMode = "server";
	public const string LocalMode = "local";
	public const string UpstreamPrerelease = "upstream";
	public const int MaxReleaseBranches = 16;
	public const int MaxLinePrerelease = 32;
	public const int MaxIgnore = 64;
	public const int MaxGroups = 64;
	public const int MaxGroupNameLength = 100;
	public const int MaxBranchPatternLength = 255;

	public string? Mode { get; set; }
	public string? Dispatch { get; set; }
	public List<string>? ReleaseBranches { get; set; }
	public List<string>? Ignore { get; set; }

	/// <summary>
	/// A label every package releases with (D8), whatever it ships against; <c>stable</c> releases without a label
	/// (versioning-beta-upstream-PRD G2); <c>upstream</c> or absent mirrors upstream.
	/// </summary>
	public string? Prerelease { get; set; }

	public List<VersioningGroupConfig>? Groups { get; set; }
	public Dictionary<string, VersioningPackageConfig?>? Packages { get; set; }

	/// <summary>#568 D15: present (even empty) turns release lines on; absent keeps default-branch releases (D18).</summary>
	public VersioningLinesConfig? Lines { get; set; }

	/// <summary>
	/// #568 D10b: how an in-repo dependency (ProjectReference) packs: <c>minimum</c> (absent: <c>&gt;= x</c>) or
	/// <c>major-bounded</c> (<c>[x, nextMajor)</c>). The versioning action hands it to the pack as the MSBuild property
	/// <c>MintPlayerVersioningDependencyRanges</c>.
	/// </summary>
	public string? DependencyRanges { get; set; }

	public const string MinimumRanges = "minimum";
	public const string MajorBoundedRanges = "major-bounded";

	public string EffectiveMode => Mode ?? ServerMode;

	public IReadOnlyList<string> EffectiveReleaseBranches => ReleaseBranches is { Count: > 0 } ? ReleaseBranches : Releases.ReleaseBranches.Default;

	/// <summary>Every problem with the section, in the file's terms; empty when it is valid.</summary>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if (Mode is not (null or ServerMode or LocalMode))
			errors.Add($"versioning.mode must be '{ServerMode}' or '{LocalMode}'.");
		if (Dispatch is not null && !IsWorkflowFile(Dispatch))
			errors.Add("versioning.dispatch must be a workflow file name under .github/workflows, such as publish.yml.");
		if (!IsPrerelease(Prerelease))
			errors.Add($"versioning.prerelease must be one of {PinValues}.");
		if (DependencyRanges is not (null or MinimumRanges or MajorBoundedRanges))
			errors.Add($"versioning.dependencyRanges must be '{MinimumRanges}' or '{MajorBoundedRanges}'.");

		if (ReleaseBranches is { } branches)
		{
			if (branches.Count > MaxReleaseBranches) errors.Add($"versioning.releaseBranches has more than {MaxReleaseBranches} patterns.");
			foreach (var pattern in branches.Take(MaxReleaseBranches))
				if (!IsBranchPattern(pattern))
					errors.Add($"versioning.releaseBranches: '{Shown(pattern)}' is not a branch pattern (letters, digits, '/', '.', '_', '-' and '*').");
		}

		if (Ignore is { } ignore)
		{
			if (ignore.Count > MaxIgnore) errors.Add($"versioning.ignore has more than {MaxIgnore} globs.");
			foreach (var glob in ignore.Take(MaxIgnore))
				if (!IsGlob(glob))
					errors.Add($"versioning.ignore: '{Shown(glob)}' is not a valid glob.");
		}

		if (Groups is { } groups)
		{
			if (groups.Count > MaxGroups) errors.Add($"versioning.groups has more than {MaxGroups} groups.");
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var (group, i) in groups.Take(MaxGroups).Select((g, i) => (g, i)))
			{
				if (group?.Name is not { Length: > 0 and <= MaxGroupNameLength } name || name.Any(char.IsControl))
					errors.Add($"versioning.groups[{i}].name is missing or longer than {MaxGroupNameLength} characters.");
				else if (!names.Add(name))
					errors.Add($"versioning.groups[{i}]: the name '{name}' is used twice.");
				if (group?.Packages is not { Count: > 0 and <= Limits.MaxPackagesPerRun } packages)
					errors.Add($"versioning.groups[{i}].packages must list 1 to {Limits.MaxPackagesPerRun} package patterns.");
				else if (packages.FirstOrDefault(p => !IsPackagePattern(p)) is { } bad)
					errors.Add($"versioning.groups[{i}].packages: '{Shown(bad)}' is not a package id pattern.");
			}
		}

		if (Packages is { } packageSettings)
		{
			if (packageSettings.Count > Limits.MaxPackagesPerRun) errors.Add($"versioning.packages has more than {Limits.MaxPackagesPerRun} entries.");
			foreach (var (id, settings) in packageSettings.Take(Limits.MaxPackagesPerRun))
			{
				if (!Identifiers.IsPackageId(id))
					errors.Add($"versioning.packages: '{Shown(id)}' is not a package id.");
				else if (settings?.Anchor is { } anchor && anchor != "free" && !Identifiers.IsPackageId(anchor))
					errors.Add($"versioning.packages.{id}.anchor must be 'free' or the id of the package whose major it follows.");
				else if (!IsPrerelease(settings?.Prerelease))
					errors.Add($"versioning.packages.{id}.prerelease must be one of {PinValues}.");
				else if (settings?.Line is { } line && !Ecosystems.IsName(line))
					errors.Add($"versioning.packages.{id}.line must be an ecosystem name such as dotnet or angular (lower-case letters and digits).");
			}
		}

		if (Lines is { } lines && ReleaseLinePattern.TryParse(lines.Pattern ?? ReleaseLinePattern.DefaultText, out var patternError) is null)
			errors.Add(patternError!);
		if (Lines?.Prerelease is { } linePrerelease)
		{
			if (linePrerelease.Count > MaxLinePrerelease) errors.Add($"versioning.lines.prerelease has more than {MaxLinePrerelease} lines.");
			foreach (var (key, setting) in linePrerelease.Take(MaxLinePrerelease))
				if (ReleaseLine.TryParse(key) is null)
					errors.Add($"versioning.lines.prerelease: '{Shown(key)}' is not a line such as dotnet-11.x.");
				else if (!IsPrerelease(setting) || setting is null)
					errors.Add($"versioning.lines.prerelease.{key} must be one of {PinValues}.");
		}
		if (Lines?.Current is { } current && current is not (VersioningLinesConfig.Promote or VersioningLinesConfig.Backport))
			errors.Add($"versioning.lines.current must be '{VersioningLinesConfig.Promote}' or '{VersioningLinesConfig.Backport}'.");
		if (Lines?.Past is { } past && past != VersioningLinesConfig.Backport)
			errors.Add($"versioning.lines.past must be '{VersioningLinesConfig.Backport}'.");
		if (Lines?.Trigger is { } trigger && trigger != VersioningLinesConfig.Manual)
			errors.Add($"versioning.lines.trigger must be '{VersioningLinesConfig.Manual}'.");
		if (Lines?.Workflow is { } workflow && !IsWorkflowFile(workflow))
			errors.Add("versioning.lines.workflow must be a workflow file name under .github/workflows, such as publish.yml.");
		else if (Lines is { Current: VersioningLinesConfig.Promote, Workflow: null })
			errors.Add("versioning.lines.workflow is required with current: promote: it names the workflow whose success on the default branch makes a commit releasable.");
		return errors;
	}

	/// <summary>What the release set needs (<see cref="VersioningSettings"/>). Only for a section <see cref="Validate"/> accepted.</summary>
	public VersioningSettings ToSettings() => new()
	{
		Ignore = Ignore ?? VersioningSettings.DefaultIgnore,
		Groups = (Groups ?? []).Select(g => new LockstepGroup(g.Name!, g.Packages!)).ToList(),
		Anchors = (Packages ?? []).Where(p => p.Value?.Anchor is not null).ToDictionary(p => p.Key, p => p.Value!.Anchor!, StringComparer.Ordinal),
		ReleaseBranches = EffectiveReleaseBranches,
		Prerelease = Pin(Prerelease),
		PrereleasePins = (Packages ?? []).Where(p => p.Value?.Prerelease is not null).ToDictionary(p => p.Key, p => Pin(p.Value!.Prerelease), StringComparer.Ordinal),
		Lines = EffectiveLines,
		LinePins = (Packages ?? []).Where(p => p.Value?.Line is not null).ToDictionary(p => p.Key, p => p.Value!.Line!, StringComparer.Ordinal),
		LinePrerelease = (Lines?.Prerelease ?? []).ToDictionary(p => ReleaseLine.TryParse(p.Key)!.ToString(), p => Pin(p.Value), StringComparer.Ordinal),
	};

	/// <summary>
	/// Whether a run on <paramref name="branch"/> versions: the default branch, a release line when lines are on (#568 D1),
	/// else a <c>releaseBranches</c> match (D10). <paramref name="branchMajor"/> is the line's or release branch's major.
	/// </summary>
	public bool IsVersionedBranch(string branch, string defaultBranch, out int? branchMajor)
	{
		branchMajor = null;
		if (branch == defaultBranch) return true;
		if (EffectiveLines is not { } lines) return Releases.ReleaseBranches.IsMatch(EffectiveReleaseBranches, branch, out branchMajor);
		if (!lines.TryMatch(branch, out var line)) return false;
		branchMajor = line!.Major;
		return true;
	}

	/// <summary>What a refusal names as the versioned branches.</summary>
	public string VersionedBranchesText => EffectiveLines is { } lines ? lines.Text : string.Join(", ", EffectiveReleaseBranches);

	/// <summary>The line pattern when release lines are on (#568 D15), else null. Only for a section <see cref="Validate"/> accepted.</summary>
	public ReleaseLinePattern? EffectiveLines => Lines is null ? null : ReleaseLinePattern.Parse(Lines.Pattern ?? ReleaseLinePattern.DefaultText);

	private static PrereleasePin Pin(string? setting) => PrereleasePin.TryParse(setting, out var pin) ? pin : PrereleasePin.Upstream;

	private static bool IsPrerelease(string? setting) => setting is null || PrereleasePin.TryParse(setting, out _);

	private const string PinValues = "alpha, beta, next, preview, rc, stable or upstream";

	private static bool IsWorkflowFile(string name)
		=> name.Length is > 0 and <= 255 && (name.EndsWith(".yml", StringComparison.Ordinal) || name.EndsWith(".yaml", StringComparison.Ordinal))
			&& name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !name.StartsWith('.');

	/// <summary>A branch name or pattern safe to name a ref with (the git writer re-checks what it receives, D12).</summary>
	public static bool IsBranchPattern(string? pattern)
		=> pattern is { Length: > 0 and <= MaxBranchPatternLength } && pattern.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '*')
			&& !pattern.StartsWith('/') && !pattern.EndsWith('/') && !pattern.Contains("..", StringComparison.Ordinal) && !pattern.Contains("//", StringComparison.Ordinal);

	private static bool IsPackagePattern(string? pattern)
		=> pattern is { Length: > 0 and <= Limits.MaxGlobLength } && !pattern.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

	private static bool IsGlob(string? glob)
	{
		try
		{
			return glob is not null && new PathGlob(glob) is not null;
		}
		catch (GraphException)
		{
			return false;
		}
	}

	/// <summary>A value quoted in an error: a check-run summary is shown to the repository's readers, so it is bounded and printable.</summary>
	private static string Shown(string? value)
		=> value is null ? "(null)" : new string(value.Take(80).Select(c => char.IsControl(c) ? '?' : c).ToArray());
}

public sealed class VersioningGroupConfig
{
	public string? Name { get; set; }
	public List<string>? Packages { get; set; }
}

public sealed class VersioningPackageConfig
{
	/// <summary><c>free</c>, or the id of the package whose resolved major this one follows (D7).</summary>
	public string? Anchor { get; set; }

	/// <summary>A label (D8) for this package alone, or <c>upstream</c> to exempt it from <c>versioning.prerelease</c>.</summary>
	public string? Prerelease { get; set; }

	/// <summary>#568 D19: the ecosystem whose lines release this package, when the default (NuGet → dotnet, npm → its framework) is wrong.</summary>
	public string? Line { get; set; }
}

/// <summary>The <c>versioning.lines</c> block (#568 D2, D15).</summary>
public sealed class VersioningLinesConfig
{
	/// <summary>The branch pattern with <c>{ecosystem}</c> and <c>{major}</c>; null is <see cref="ReleaseLinePattern.DefaultText"/>.</summary>
	public string? Pattern { get; set; }

	/// <summary>
	/// #568 D14: the prerelease label of one line, keyed <c>{ecosystem}-{major}.x</c> (<c>dotnet-11.x: upstream</c>), so a
	/// line goes stable, or stays on a label, apart from <c>versioning.prerelease</c>. A package's own pin still wins.
	/// </summary>
	public Dictionary<string, string>? Prerelease { get; set; }

	/// <summary>
	/// Segment P (P0): how a current line (the highest major among its ecosystem's line branches) takes the default
	/// branch's changes. <see cref="Promote"/> moves it to a green default-branch commit on <c>/release</c>;
	/// <see cref="Backport"/> (also null) cherry-picks labelled pull requests onto it.
	/// </summary>
	public string? Current { get; set; }

	/// <summary>How the older lines take changes; only <see cref="Backport"/> for now (null is the same).</summary>
	public string? Past { get; set; }

	/// <summary>What starts a promotion; only <see cref="Manual"/> (<c>/release</c>) for now (null is the same).</summary>
	public string? Trigger { get; set; }

	/// <summary>
	/// P2: the workflow file (under <c>.github/workflows</c>) whose run on the default branch marks a commit green; a
	/// promotion moves a line only to a commit whose run of it concluded <c>success</c>. Required with <c>current: promote</c>.
	/// </summary>
	public string? Workflow { get; set; }

	public const string Promote = "promote";
	public const string Backport = "backport";
	public const string Manual = "manual";

	/// <summary>Whether current lines are promoted (P0). Only for a section <see cref="VersioningConfig.Validate"/> accepted.</summary>
	public bool PromotesCurrent => Current == Promote;
}
