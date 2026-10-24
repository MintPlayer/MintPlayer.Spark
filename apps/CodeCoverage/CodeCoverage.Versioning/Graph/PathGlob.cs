namespace CodeCoverage.Versioning.Graph;

/// <summary>
/// A bounded path glob (PRD D5), matched segment by segment and never turned into a regex: <c>**</c> spans any number of
/// directories, <c>*</c> and <c>?</c> stay within one segment, matching is case-sensitive like git paths. A leading
/// <c>./</c> is dropped, and a glob without a slash matches a file name at any depth (<c>*.md</c> = <c>**/*.md</c>), as
/// in .gitignore. The match is a memoised dynamic program, so its cost is bounded by the lengths of glob and path, not
/// exponential in the number of stars.
/// </summary>
public sealed class PathGlob
{
	private readonly string[] segments;

	public PathGlob(string glob)
	{
		if (string.IsNullOrWhiteSpace(glob) || glob.Length > Limits.MaxGlobLength)
			throw new GraphException($"The glob '{glob}' is empty or longer than {Limits.MaxGlobLength} characters.");
		var text = glob.Trim();
		if (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
		text = text.TrimStart('/').TrimEnd('/');
		if (!text.Contains('/') && text != "**") text = "**/" + text;
		segments = text.Split('/');
		if (segments.Length > Limits.MaxGlobSegments)
			throw new GraphException($"The glob '{glob}' has more than {Limits.MaxGlobSegments} segments.");
		if (segments.Any(s => s.Contains("**") && s != "**"))
			throw new GraphException($"The glob '{glob}' uses ** inside a segment; ** must be a whole segment.");
		Glob = glob;
	}

	public string Glob { get; }

	public bool Matches(string path)
	{
		var parts = path.Split('/');
		var memo = new bool?[segments.Length + 1, parts.Length + 1];
		return Match(0, 0);

		bool Match(int g, int p)
		{
			if (memo[g, p] is { } known) return known;
			bool result;
			if (g == segments.Length) result = p == parts.Length;
			else if (segments[g] == "**") result = Match(g + 1, p) || p < parts.Length && Match(g, p + 1);
			else result = p < parts.Length && Segment(segments[g], parts[p]) && Match(g + 1, p + 1);
			memo[g, p] = result;
			return result;
		}
	}

	/// <summary>Whether one path segment matches one glob segment of <c>*</c>, <c>?</c> and literals.</summary>
	public static bool Segment(string pattern, string text)
	{
		// Classic two-pointer wildcard match with one backtrack point: linear-ish, no recursion.
		int p = 0, t = 0, star = -1, mark = 0;
		while (t < text.Length)
		{
			if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
			else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
			else if (star >= 0) { p = star + 1; t = ++mark; }
			else return false;
		}
		while (p < pattern.Length && pattern[p] == '*') p++;
		return p == pattern.Length;
	}

	public override string ToString() => Glob;
}
