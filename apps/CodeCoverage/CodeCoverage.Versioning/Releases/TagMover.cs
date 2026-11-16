using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <param name="ExpectedObject">The remote ref's value the move leases on: the tag object the caller saw.</param>
public sealed record TagMove(VersionTag Tag, string Commit, string ExpectedObject, TagAnnotation Annotation);

/// <param name="Moved">False when the remote's tag was no longer <see cref="TagMove.ExpectedObject"/>; <see cref="Detail"/> says why.</param>
public sealed record TagMoveResult(VersionTag Tag, bool Moved, string? Detail = null);

/// <summary>
/// #638 D9: moves tags the App made onto their version's earliest occurrence, once the owner chose it. This is the only
/// place a version tag is force-pushed (<see cref="TagWriter"/> never is, M5), and each push is leased on the tag object the
/// caller read, so a tag that changed since, a person's re-tag among them, is rejected and left alone.
/// </summary>
public sealed class TagMover(ITagGit git, string remote, Tagger tagger)
{
	public async Task<IReadOnlyList<TagMoveResult>> MoveAsync(IReadOnlyList<TagMove> moves, CancellationToken cancellationToken = default)
	{
		if (moves.Count == 0) return [];
		if (moves.Count > Graph.Limits.MaxPackagesPerRun) throw new VersioningException($"A run moves at most {Graph.Limits.MaxPackagesPerRun} tags.");
		if (moves.Select(m => m.Tag.Name).Distinct(StringComparer.Ordinal).Count() != moves.Count) throw new VersioningException("A tag is listed twice.");

		foreach (var move in moves)
		{
			GitCli.RequireObjectId(move.Commit);
			GitCli.RequireObjectId(move.ExpectedObject);
			(await git.RunAsync(["tag", "--force", "--annotate", "--no-sign", "--cleanup=verbatim", "--file=-", "--end-of-options", move.Tag.Name, move.Commit],
				move.Annotation.Format(move.Tag), authenticated: false, tagger, cancellationToken)).EnsureSuccess("tag");
		}

		// No '+' on the refspecs: a forced refspec would override the lease.
		string[] arguments = ["push", "--porcelain", "--no-verify", "--no-recurse-submodules",
			.. moves.Select(m => $"--force-with-lease=refs/tags/{m.Tag.Name}:{m.ExpectedObject}"),
			"--", remote, .. moves.Select(m => $"refs/tags/{m.Tag.Name}:refs/tags/{m.Tag.Name}")];
		var result = await git.RunAsync(arguments, null, authenticated: true, null, cancellationToken);
		var reported = new Dictionary<string, (bool Rejected, string Summary)>(StringComparer.Ordinal);
		foreach (var line in result.Text.Split('\n'))
		{
			var parts = line.TrimEnd('\r').Split('\t');
			if (parts.Length < 2) continue;
			var colon = parts[1].IndexOf(':');
			if (colon < 0) continue;
			var name = parts[1][(colon + 1)..];
			if (!name.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;
			reported[name["refs/tags/".Length..]] = (parts[0].StartsWith('!'), parts.Length > 2 ? parts[2] : "");
		}
		if (result.ExitCode != 0 && !reported.Values.Any(r => r.Rejected)) result.EnsureSuccess("push");

		var results = new List<TagMoveResult>();
		foreach (var move in moves)
		{
			if (!reported.TryGetValue(move.Tag.Name, out var outcome))
				throw new GitException($"git push did not report {move.Tag.Name}: {result.Error.Trim()}");
			// D3 (docs/onboarding_backfill_partial_push_PRD.md): only a failed lease means the tag changed since it was read.
			results.Add(!outcome.Rejected ? new TagMoveResult(move.Tag, true)
				: outcome.Summary.Contains("stale info", StringComparison.Ordinal)
					? new TagMoveResult(move.Tag, false, $"{move.Tag.Name} changed since it was read ({outcome.Summary.Trim()}); it was left alone.")
					: new TagMoveResult(move.Tag, false,
						$"the remote refused to move {move.Tag.Name}: {outcome.Summary.Trim()}{TagWriter.Remote(result.Error, move.Tag.Name)}; it was left alone."));
		}
		return results;
	}
}
