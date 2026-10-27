using System.Text.Json;
using System.Text.Json.Serialization;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Versioning.Cli;

/// <summary>The JSON both hosts speak with the action: camelCase, nulls left out.</summary>
internal static class Wire
{
	public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
	};
}

/// <summary>
/// The local run's result in the shape of the server's <c>POST /api/versioning/run</c> response
/// (<c>VersioningController.VersioningRunResponse</c>), so the action reads one format whichever host ran.
/// </summary>
internal sealed record LocalRunResponse(
	string Outcome, string? Reason, string? Detail, IReadOnlyList<LocalRelease> Releases, IReadOnlyList<LocalPackageNote> Refused,
	int Skipped, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Errors = null, IReadOnlyList<ReleaseBranchCut>? BranchCuts = null)
{
	/// <summary>The tags a plan-only run decided (#552 D1); <c>push-tags</c> writes them.</summary>
	public TagPlan? Plan { get; init; }

	public const string Versioned = "versioned";
	public const string PlanRefused = "plan-refused";
	public const string RefusedOutcome = "refused";

	public static LocalRunResponse Refusal(string reason, string? detail, IReadOnlyList<string>? errors = null)
		=> new(RefusedOutcome, reason, detail, [], [], 0, [], errors);

	public static LocalRunResponse From(ReleaseRunResult result)
	{
		var outcomes = result.Tags.ToDictionary(t => t.Tag.Name, StringComparer.Ordinal);
		return new LocalRunResponse(
			result.Plan.Refused.Count > 0 ? PlanRefused : Versioned, null, null,
			[.. result.Plan.Releases.Select(r => new LocalRelease(r.Id, ManifestPackage.Wire(r.Project.Kind), r.Version.ToString(), r.Tag.Name, r.Reasons,
				r.ApiHash, r.TaggedByHand, outcomes.TryGetValue(r.Tag.Name, out var written) ? TagWire(written.Outcome) : null,
				result.Notes?.GetValueOrDefault(r.Tag.Name) is { } notes ? new LocalReleaseNotes(notes.Name, notes.Body, notes.Prerelease) : null))],
			[.. result.Plan.Refused.Select(n => new LocalPackageNote(n.Id, n.Reason))],
			result.Plan.Skipped.Count, result.Warnings, null, result.BranchCuts);
	}

	/// <summary>
	/// This plan after <c>push-tags</c> (#552 D2): each tag's outcome, and only the notes and branch cuts of tags now in
	/// place, as <see cref="ReleaseRun.RunAsync"/> computes them for a run that wrote its tags itself.
	/// </summary>
	public LocalRunResponse Released(IReadOnlyList<TagWriteResult> written)
	{
		var outcomes = written.ToDictionary(t => t.Tag.Name, StringComparer.Ordinal);
		var releases = Releases.Select(r => r.TaggedByHand ? r
			: outcomes.TryGetValue(r.Tag, out var result) ? r with
			{
				TagOutcome = TagWire(result.Outcome),
				Release = result.Outcome is TagOutcome.Created or TagOutcome.AlreadyPresent ? r.Release : null,
			}
			: r with { TagOutcome = null, Release = null }).ToList();
		var placed = releases.Where(r => r.Release is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
		var failed = written.Any(t => t.Outcome is not (TagOutcome.Created or TagOutcome.AlreadyPresent));
		return this with
		{
			Releases = releases,
			BranchCuts = BranchCuts?.Where(c => c.Packages.Any(placed.Contains)).ToList(),
			Errors = failed ? [.. written.Where(t => t.Outcome is not (TagOutcome.Created or TagOutcome.AlreadyPresent)).Select(t => $"{t.Tag.Name}: {t.Detail ?? t.Outcome.ToString()}")] : Errors,
			Plan = null,
		};
	}

	private static string TagWire(TagOutcome outcome) => outcome switch
	{
		TagOutcome.Created => "created",
		TagOutcome.AlreadyPresent => "already-present",
		TagOutcome.Conflict => "conflict",
		TagOutcome.Unreachable => "unreachable",
		TagOutcome.Planned => "planned",
		_ => throw new ArgumentOutOfRangeException(nameof(outcome)),
	};
}

/// <param name="Release">The GitHub Release the action creates for the tag (D15); null when the tag is not in place.</param>
internal sealed record LocalRelease(string Id, string Kind, string Version, string Tag, IReadOnlyList<string> Reasons, string? ApiHash, bool TaggedByHand, string? TagOutcome,
	LocalReleaseNotes? Release);

internal sealed record LocalReleaseNotes(string Name, string Body, bool Prerelease);

internal sealed record LocalPackageNote(string Id, string Reason);

internal sealed class OptionsException(string message) : Exception(message);

/// <summary><c>--name value</c> pairs and bare <c>--flag</c>s.</summary>
internal sealed class Options
{
	private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

	public static Options Parse(IReadOnlyList<string> args)
	{
		var options = new Options();
		for (var i = 0; i < args.Count; i++)
		{
			if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new OptionsException($"Unexpected argument '{args[i]}'.");
			var name = args[i][2..];
			var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
			if (!options.values.TryAdd(name, value)) throw new OptionsException($"--{name} is given twice.");
		}
		return options;
	}

	public string? Get(string name) => values.GetValueOrDefault(name);

	public bool Has(string name) => values.ContainsKey(name);

	public string Require(string name) => Get(name) ?? throw new OptionsException($"--{name} is required.");
}
