using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Validation;

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
///   releaseBranches: ["release/*.x"] # D10
///   ignore: ["**/*.md"]              # D4: changed files that never release a package
///   groups:                          # D13
///     - name: mapper
///       packages: [MintPlayer.Mapper, MintPlayer.Mapper.*]
///   packages:
///     MintPlayer.SourceGenerators: { anchor: free }   # D7: free, or a framework package id
/// </code>
/// </remarks>
public sealed class VersioningConfig
{
	public const string ServerMode = "server";
	public const string LocalMode = "local";
	public const int MaxReleaseBranches = 16;
	public const int MaxIgnore = 64;
	public const int MaxGroups = 64;
	public const int MaxGroupNameLength = 100;
	public const int MaxBranchPatternLength = 255;

	public string? Mode { get; set; }
	public string? Dispatch { get; set; }
	public List<string>? ReleaseBranches { get; set; }
	public List<string>? Ignore { get; set; }
	public List<VersioningGroupConfig>? Groups { get; set; }
	public Dictionary<string, VersioningPackageConfig?>? Packages { get; set; }

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
			}
		}
		return errors;
	}

	/// <summary>What the release set needs (<see cref="VersioningSettings"/>). Only for a section <see cref="Validate"/> accepted.</summary>
	public VersioningSettings ToSettings() => new()
	{
		Ignore = Ignore ?? VersioningSettings.DefaultIgnore,
		Groups = (Groups ?? []).Select(g => new LockstepGroup(g.Name!, g.Packages!)).ToList(),
		Anchors = (Packages ?? []).Where(p => p.Value?.Anchor is not null).ToDictionary(p => p.Key, p => p.Value!.Anchor!, StringComparer.Ordinal),
		ReleaseBranches = EffectiveReleaseBranches,
	};

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
}
