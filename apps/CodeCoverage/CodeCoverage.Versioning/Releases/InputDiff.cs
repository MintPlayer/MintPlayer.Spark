using System.Text;
using CodeCoverage.Versioning.Git;

namespace CodeCoverage.Versioning.Releases;

/// <summary>The files that differ between two commits below the given paths, read from trees alone (D4).</summary>
public interface IInputDiff
{
	Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

/// <summary><c>git diff-tree</c> on a bare repository: no checkout, pathspecs taken literally.</summary>
public sealed class GitInputDiff(GitCli git) : IInputDiff
{
	public async Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		if (paths.Count == 0) return [];
		string[] arguments = ["diff-tree", "-r", "--name-only", "-z", "--no-renames", GitCli.RequireObjectId(from), GitCli.RequireObjectId(to), "--",
			.. paths.Select(p => ":(literal)" + p)];
		var result = (await git.RunAsync(arguments, cancellationToken: cancellationToken)).EnsureSuccess("diff-tree");
		return Encoding.UTF8.GetString(result.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
	}
}
