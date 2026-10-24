using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Api;

/// <summary>
/// The public API of an npm package as text lines (PRD D6, plan S3): its <c>.d.ts</c> files, sorted by path, with
/// comments dropped, whitespace collapsed around punctuation and <c>private</c> members left out (the compiler emits
/// them as <c>private x;</c>, but no consumer can reach them). TypeScript 5.8 and 5.9 emitted the same <c>.d.ts</c> for
/// the same source (S3), so the lines change only when a declaration a consumer sees does.
/// </summary>
public static partial class TypeScriptApiListing
{
	/// <param name="files">Each declaration file by its path relative to the package root, with <c>/</c> separators.</param>
	public static IReadOnlyList<string> List(IEnumerable<(string Path, string Text)> files)
	{
		var lines = new List<string>();
		foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
		{
			var normalized = Punctuation().Replace(Whitespace().Replace(StripComments(text), " "), "$1");
			normalized = PrivateMember().Replace(normalized, "");
			foreach (var statement in Statements().Split(normalized))
				if (statement.Trim() is { Length: > 0 } line) lines.Add($"{path}: {line}");
		}
		return lines;
	}

	public static string Hash(IEnumerable<string> lines) => DotnetApiListing.Hash(lines);

	/// <summary>Comments out, string and template literals kept as written (a literal type can hold <c>//</c>).</summary>
	internal static string StripComments(string text)
	{
		var result = new StringBuilder(text.Length);
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c is '"' or '\'' or '`')
			{
				var end = i + 1;
				while (end < text.Length && text[end] != c) end += text[end] == '\\' ? 2 : 1;
				end = Math.Min(end, text.Length - 1);
				result.Append(text, i, end - i + 1);
				i = end;
			}
			else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
			{
				while (i < text.Length && text[i] != '\n') i++;
				result.Append('\n');
			}
			else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
			{
				var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
				i = end < 0 ? text.Length : end + 1;
				result.Append(' ');
			}
			else result.Append(c);
		}
		return result.ToString();
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	[GeneratedRegex(@" ?([{}()\[\];,:<>=|&?]) ?")]
	private static partial Regex Punctuation();

	/// <summary>A <c>private</c> property or method in a class body; a <c>private constructor</c> stays, it forbids <c>new</c>.</summary>
	[GeneratedRegex(@"(?<=[{;])private (?:static |readonly |abstract )*(?!constructor\b)[^;{}]*;")]
	private static partial Regex PrivateMember();

	[GeneratedRegex(@"(?<=[;{}])")]
	private static partial Regex Statements();
}
