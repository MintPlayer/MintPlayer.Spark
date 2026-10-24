namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// Repository-relative paths (PRD D5): <c>/</c>-separated, no leading slash, no <c>.</c> or <c>..</c> segments. A path a
/// consumer wrote (a <c>ProjectReference</c>, a <c>file:</c> dependency, a linked item) is resolved against its file's
/// directory and refused when it climbs out of the repository.
/// </summary>
public static class RepoPath
{
	public static string Directory(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? "" : path[..slash];
	}

	public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

	public static string Combine(string directory, string relative) => directory.Length == 0 ? relative : directory + "/" + relative;

	/// <summary>
	/// Resolves <paramref name="relative"/> against <paramref name="directory"/>, or returns null when the result is
	/// outside the repository, absolute, a drive path, a UNC path, or contains characters no repository path has.
	/// </summary>
	public static string? Resolve(string directory, string relative)
	{
		if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024) return null;
		var text = relative.Trim().Replace('\\', '/');
		if (text.StartsWith('/') || text.Contains(':') || text.Contains('\0') || text.Contains('\n')) return null;

		var segments = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
		foreach (var segment in text.Split('/'))
		{
			if (segment is "" or ".") continue;
			if (segment == "..")
			{
				if (segments.Count == 0) return null;
				segments.RemoveAt(segments.Count - 1);
			}
			else segments.Add(segment);
		}
		return string.Join('/', segments);
	}

	/// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or below it.</summary>
	public static bool IsUnder(string path, string directory)
		=> directory.Length == 0 || path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

	/// <summary>The directories from the repository root down to <paramref name="directory"/>, nearest last.</summary>
	public static IEnumerable<string> Ancestry(string directory)
	{
		yield return "";
		if (directory.Length == 0) yield break;
		var at = 0;
		while ((at = directory.IndexOf('/', at)) >= 0)
		{
			yield return directory[..at];
			at++;
		}
		yield return directory;
	}
}
