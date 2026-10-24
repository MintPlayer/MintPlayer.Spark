using System.Text;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One version tag a run writes: the annotated tag <see cref="Tag"/> on <see cref="Commit"/> (D1, D6).</summary>
public sealed record TagToWrite(VersionTag Tag, string Commit, TagAnnotation Annotation);

/// <summary>The person a tag is made by: the tagger of the annotated tag.</summary>
public sealed record Tagger(string Name, string Email);

public enum TagOutcome
{
	/// <summary>The tag was created and pushed.</summary>
	Created,

	/// <summary>The remote already had the tag on the same commit: a rerun, nothing written.</summary>
	AlreadyPresent,

	/// <summary>The remote has the tag on another commit. A version is never moved.</summary>
	Conflict,

	/// <summary>The commit is not on the default branch or a release branch, so it is never tagged.</summary>
	Unreachable,
}

public sealed record TagWriteResult(VersionTag Tag, TagOutcome Outcome, string? Detail = null);

/// <summary>Writes a run's tags. Both hosts implement it over <see cref="TagWriter"/>, so they decide alike (M5, M7).</summary>
public interface ITagWriter
{
	Task<IReadOnlyList<TagWriteResult>> WriteAsync(IReadOnlyList<TagToWrite> tags, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="TagWriter"/> needs of git: the host decides how a command runs and how it authenticates.</summary>
public interface ITagGit
{
	/// <param name="authenticated">The command talks to the remote and needs the host's credential.</param>
	/// <param name="tagger">Set for the command that creates an annotated tag.</param>
	Task<GitCli.Result> RunAsync(IReadOnlyList<string> arguments, string? input, bool authenticated, Tagger? tagger, CancellationToken cancellationToken);
}

/// <summary>
/// Creates annotated version tags in a repository and pushes them, never with force (M5). A rerun is a no-op: a tag the
/// remote already holds on the same commit is reported, one it holds on another commit is a conflict and is left alone.
/// A commit that no branch in <paramref name="branchRefs"/> contains is refused: tags go only on commits of the default
/// branch or a release branch (D10).
/// </summary>
/// <param name="remote">The remote's name or URL.</param>
/// <param name="branchRefs">Full ref names in the local repository whose commits may be tagged.</param>
public sealed class TagWriter(ITagGit git, string remote, IReadOnlyList<string> branchRefs, Tagger tagger) : ITagWriter
{
	public async Task<IReadOnlyList<TagWriteResult>> WriteAsync(IReadOnlyList<TagToWrite> tags, CancellationToken cancellationToken = default)
	{
		if (tags.Count > Graph.Limits.MaxPackagesPerRun) throw new VersioningException($"A run writes at most {Graph.Limits.MaxPackagesPerRun} tags.");
		if (tags.Select(t => t.Tag.Name).Distinct(StringComparer.Ordinal).Count() != tags.Count) throw new VersioningException("A tag is listed twice.");

		var results = new Dictionary<string, TagWriteResult>(StringComparer.Ordinal);
		var remoteTags = await RemoteTagsAsync(tags, cancellationToken);
		var toPush = new List<TagToWrite>();
		foreach (var tag in tags)
		{
			GitCli.RequireObjectId(tag.Commit);
			if (remoteTags.TryGetValue(tag.Tag.Name, out var existing))
			{
				results[tag.Tag.Name] = existing == tag.Commit
					? new TagWriteResult(tag.Tag, TagOutcome.AlreadyPresent)
					: new TagWriteResult(tag.Tag, TagOutcome.Conflict, $"{tag.Tag.Name} already points at {existing}.");
				continue;
			}
			if (!await ReachableAsync(tag.Commit, cancellationToken))
			{
				results[tag.Tag.Name] = new TagWriteResult(tag.Tag, TagOutcome.Unreachable, $"{tag.Commit} is not on {string.Join(", ", branchRefs)}.");
				continue;
			}
			await CreateLocalAsync(tag, cancellationToken);
			toPush.Add(tag);
		}

		if (toPush.Count > 0)
			foreach (var result in await PushAsync(toPush, cancellationToken))
				results[result.Tag.Name] = result;
		return tags.Select(t => results[t.Tag.Name]).ToList();
	}

	/// <summary>The commits the remote's tags point at (peeled), for the tags of this run only.</summary>
	private async Task<Dictionary<string, string>> RemoteTagsAsync(IEnumerable<TagToWrite> tags, CancellationToken cancellationToken)
	{
		string[] arguments = ["ls-remote", "--tags", "--", remote, .. tags.SelectMany(t => new[] { "refs/tags/" + t.Tag.Name, "refs/tags/" + t.Tag.Name + "^{}" })];
		var result = (await git.RunAsync(arguments, null, authenticated: true, null, cancellationToken)).EnsureSuccess("ls-remote");
		var direct = new Dictionary<string, string>(StringComparer.Ordinal);
		var peeled = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var tab = line.IndexOf('\t');
			if (tab <= 0 || !GitCli.IsObjectId(line[..tab])) continue;
			var name = line[(tab + 1)..];
			if (!name.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;
			name = name["refs/tags/".Length..];
			if (name.EndsWith("^{}", StringComparison.Ordinal)) peeled[name[..^3]] = line[..tab];
			else direct[name] = line[..tab];
		}
		foreach (var (name, commit) in peeled) direct[name] = commit;
		return direct;
	}

	private async Task<bool> ReachableAsync(string commit, CancellationToken cancellationToken)
	{
		foreach (var branch in branchRefs)
		{
			var result = await git.RunAsync(["merge-base", "--is-ancestor", commit, branch], null, authenticated: false, null, cancellationToken);
			if (result.ExitCode == 0) return true;
			if (result.ExitCode != 1) result.EnsureSuccess("merge-base");
		}
		return false;
	}

	/// <summary>Creates the annotated tag locally, or keeps one a previous attempt left on the same commit.</summary>
	private async Task CreateLocalAsync(TagToWrite tag, CancellationToken cancellationToken)
	{
		var reference = "refs/tags/" + tag.Tag.Name;
		var local = await git.RunAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"], null, authenticated: false, null, cancellationToken);
		if (local.ExitCode == 0)
		{
			if (local.Text.Trim() == tag.Commit) return;
			// A local tag the remote does not have, on another commit: a leftover of a failed run, never published.
			(await git.RunAsync(["tag", "--delete", tag.Tag.Name], null, authenticated: false, null, cancellationToken)).EnsureSuccess("tag --delete");
		}
		(await git.RunAsync(["tag", "--annotate", "--no-sign", "--cleanup=verbatim", "--file=-", "--end-of-options", tag.Tag.Name, tag.Commit],
			tag.Annotation.Format(tag.Tag), authenticated: false, tagger, cancellationToken)).EnsureSuccess("tag");
	}

	/// <summary>
	/// One push of every new tag, without force: a tag another run pushed meanwhile is rejected, then read back, so a
	/// race between two runs of the same commit ends as <see cref="TagOutcome.AlreadyPresent"/> for the loser.
	/// </summary>
	private async Task<IReadOnlyList<TagWriteResult>> PushAsync(IReadOnlyList<TagToWrite> tags, CancellationToken cancellationToken)
	{
		string[] arguments = ["push", "--porcelain", "--no-verify", "--no-recurse-submodules", "--", remote, .. tags.Select(t => $"refs/tags/{t.Tag.Name}:refs/tags/{t.Tag.Name}")];
		var result = await git.RunAsync(arguments, null, authenticated: true, null, cancellationToken);
		var rejected = new HashSet<string>(StringComparer.Ordinal);
		var pushed = new HashSet<string>(StringComparer.Ordinal);
		foreach (var line in result.Text.Split('\n'))
		{
			var parts = line.Split('\t');
			if (parts.Length < 2) continue;
			var colon = parts[1].IndexOf(':');
			if (colon < 0) continue;
			var name = parts[1][(colon + 1)..];
			if (!name.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;
			name = name["refs/tags/".Length..];
			if (parts[0].StartsWith('!')) rejected.Add(name);
			else pushed.Add(name);
		}
		if (result.ExitCode != 0 && rejected.Count == 0) result.EnsureSuccess("push");

		var results = new List<TagWriteResult>();
		var raced = tags.Where(t => rejected.Contains(t.Tag.Name)).ToList();
		var now = raced.Count > 0 ? await RemoteTagsAsync(raced, cancellationToken) : [];
		foreach (var tag in tags)
		{
			if (pushed.Contains(tag.Tag.Name)) results.Add(new TagWriteResult(tag.Tag, TagOutcome.Created));
			else if (now.TryGetValue(tag.Tag.Name, out var commit))
				results.Add(commit == tag.Commit
					? new TagWriteResult(tag.Tag, TagOutcome.AlreadyPresent)
					: new TagWriteResult(tag.Tag, TagOutcome.Conflict, $"{tag.Tag.Name} already points at {commit}."));
			else throw new GitException($"git push did not report {tag.Tag.Name}: {result.Error.Trim()}");
		}
		return results;
	}
}

/// <summary>
/// The local host's git (M7): a checkout's repository and a token for the remote. The token travels only in the
/// environment, as an <c>http.extraHeader</c> git never persists; it is never an argument or a config file.
/// </summary>
public sealed class CliTagGit(GitCli git, string? token, string username = "x-access-token") : ITagGit
{
	public Task<GitCli.Result> RunAsync(IReadOnlyList<string> arguments, string? input, bool authenticated, Tagger? tagger, CancellationToken cancellationToken)
	{
		var environment = new Dictionary<string, string>(StringComparer.Ordinal);
		if (authenticated && !string.IsNullOrEmpty(token))
		{
			environment["GIT_CONFIG_COUNT"] = "1";
			environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
			environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
		}
		if (tagger is not null)
		{
			environment["GIT_COMMITTER_NAME"] = tagger.Name;
			environment["GIT_COMMITTER_EMAIL"] = tagger.Email;
		}
		return git.RunAsync(arguments, input is null ? null : Encoding.UTF8.GetBytes(input), environment, cancellationToken);
	}
}
