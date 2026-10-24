namespace CodeCoverage.Versioning.Versions;

/// <summary>Everything the next version of one package depends on.</summary>
/// <param name="Anchor">Where the major comes from (D7).</param>
/// <param name="LastTag">The package's last version tag reachable from the commit being released, if any.</param>
/// <param name="Existing">Every version of the package known anywhere: all its tags and its published versions.</param>
/// <param name="ApiChanged">The API hash differs from the last tag's, or could not be compared (D6).</param>
/// <param name="Upstream">The least mature prerelease label among the anchor and the runtime dependencies (D8).</param>
/// <param name="OnDefaultBranch">The commit is released from the default branch rather than a release branch.</param>
/// <param name="BranchMajor">The major a release branch is for (<c>release/10.x</c> → 10), or null on the default branch.</param>
public sealed record VersionRequest(
	MajorAnchor Anchor,
	PackageVersion? LastTag,
	IReadOnlyCollection<PackageVersion> Existing,
	bool ApiChanged,
	PrereleaseLabel? Upstream,
	bool OnDefaultBranch = true,
	int? BranchMajor = null);

public abstract record VersionDecision
{
	public sealed record Release(PackageVersion Version) : VersionDecision;

	/// <summary>Nothing to release here, and nothing wrong either.</summary>
	public sealed record Skip(string Reason) : VersionDecision;

	/// <summary>A rule was broken; the run fails with this reason in its check.</summary>
	public sealed record Refuse(string Reason) : VersionDecision;
}

/// <summary>
/// The version arithmetic of PRD D7 and D8: which major, then minor or patch by the API hash, then the prerelease label
/// mirrored from upstream with our own counter. Pure: the graph, the hashes and the tags are read by the caller.
/// </summary>
public static class VersionCalculator
{
	public static VersionDecision Next(VersionRequest request)
	{
		var anchor = request.Anchor;
		if (anchor.IsFree && !request.OnDefaultBranch)
			return new VersionDecision.Skip("A free package is released only from the default branch, so two branches never tag the same line.");

		// No anchor and no tag yet: a free package starts at 1. Onboarding seeds a tag from the version it replaced, so
		// this is only reached by a package that never had one.
		var major = anchor.Major ?? request.LastTag?.Major ?? 1;
		if (request.LastTag is { } last && major < last.Major)
			return new VersionDecision.Refuse($"The major would go down from {last.Major} to {major} ({anchor.Source}). A major never goes down; anchor the package differently with versioning.packages.<id>.anchor.");
		if (request.BranchMajor is { } branchMajor && major != branchMajor)
			return new VersionDecision.Refuse($"The package computes major {major} ({anchor.Source}) on the release branch for {branchMajor}.x.");

		var line = request.Existing.Where(v => v.Major == major).ToList();
		var highest = line.Count == 0 ? null : line.Max();
		var lastStable = line.Where(v => !v.IsPrerelease).DefaultIfEmpty().Max();

		PackageVersion core;
		if (highest is { IsPrerelease: true })
			// A prerelease line is open (11.3.0-preview.2): it continues, or goes stable as itself, whatever the API did.
			core = highest.Core;
		else if (lastStable is null)
			core = new PackageVersion(major, 0, 0);
		else if (request.Upstream is not null || request.ApiChanged)
			core = new PackageVersion(major, lastStable.Minor + 1, 0);
		else
			core = new PackageVersion(major, lastStable.Minor, lastStable.Patch + 1);

		var next = core;
		if (request.Upstream is { } label)
		{
			var counter = line.Where(v => v.Core == core && v.Label == label).Select(v => v.Number).DefaultIfEmpty(0).Max();
			next = core with { Label = label, Number = counter + 1 };
		}

		if (highest is not null && next <= highest)
			return new VersionDecision.Refuse($"{next} is not above {highest}, which already exists (upstream is less mature than the open prerelease line).");
		if (request.Existing.Contains(next))
			return new VersionDecision.Refuse($"{next} already exists.");
		return new VersionDecision.Release(next);
	}
}
