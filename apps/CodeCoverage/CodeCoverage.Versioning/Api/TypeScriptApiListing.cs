using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Api;

/// <summary>
/// The public API of an npm package as text lines (PRD D6, plan S3): its <c>.d.ts</c> files, sorted by path, with
/// comments dropped, whitespace collapsed around punctuation and <c>private</c> members left out (the compiler emits
/// them as <c>private x;</c>, but no consumer can reach them). TypeScript 5.8 and 5.9 emitted the same <c>.d.ts</c> for
/// the same source (S3), so the lines change only when a declaration a consumer sees does.
/// <para>
/// A constant's value is not part of its API (#576 D1): <c>export declare const styles = "…css…";</c> lists as
/// <c>export declare const styles: string;</c>, so a stylesheet compiled into a constant no longer changes the hash.
/// </para>
/// </summary>
public static partial class TypeScriptApiListing
{
	/// <summary>
	/// The listing's version (#576 D2): raise it whenever a change to the listing changes the lines of an unchanged API.
	/// 2 widens literal initializers (#576 D1).
	/// </summary>
	public const int Version = 2;

	/// <param name="files">Each declaration file by its path relative to the package root, with <c>/</c> separators.</param>
	public static IReadOnlyList<string> List(IEnumerable<(string Path, string Text)> files)
	{
		var lines = new List<string>();
		foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
		{
			var normalized = Punctuation().Replace(Whitespace().Replace(WidenLiteralInitializers(StripComments(text)), " "), "$1");
			normalized = PrivateMember().Replace(normalized, "");
			foreach (var statement in Statements().Split(normalized))
				if (statement.Trim() is { Length: > 0 } line) lines.Add($"{path}: {line}");
		}
		return lines;
	}

	public static string Hash(IEnumerable<string> lines) => ApiHash.Of(lines, Version);

	/// <summary>
	/// The compiler writes an initializer into a declaration only where it inferred a literal type: a <c>const</c>
	/// (<c>declare const a = 1, b = "two";</c>) and a <c>readonly</c> class property (<c>static readonly tag = "x";</c>), with a
	/// string, number, bigint or boolean (#576 S1). Each becomes its type. A literal type the author wrote
	/// (<c>const a: "x"</c>), a <c>type</c> alias and an <c>enum</c> member are declared API and stay. String and template
	/// literals are skipped whole, so declaration-like text inside one is never touched.
	/// </summary>
	internal static string WidenLiteralInitializers(string text)
		=> InitializedDeclaration().Replace(text, m => m.Groups["string"].Success ? m.Value
			: m.Groups["readonly"].Success ? $"{m.Groups["readonly"].Value}: {LiteralType(m.Groups["literal"].Value)};"
			: Declarator().Replace(m.Value, d => d.Groups["string"].Success ? d.Value : $"{d.Groups["name"].Value}: {LiteralType(d.Groups["literal"].Value)}"));

	private static string LiteralType(string literal) => literal switch
	{
		['"' or '\'', ..] => "string",
		"true" or "false" => "boolean",
		[.., 'n'] => "bigint",
		_ => "number",
	};

	private const string Literal = """(?<literal>"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|-?[0-9][0-9_]*(?:\.[0-9_]+)?(?:[eE][+-]?[0-9]+)?n?|true|false)""";
	private const string Strings = """(?<string>"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)""";

	/// <summary>A string literal (kept), a <c>declare const</c> statement, or a <c>readonly</c> property with an initializer.</summary>
	[GeneratedRegex(Strings + """|\bdeclare\s+const\s+(?:"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|[^;"'`])*;|(?<readonly>\breadonly\s+[A-Za-z_$][\w$]*)\s*=\s*""" + Literal + """\s*;""")]
	private static partial Regex InitializedDeclaration();

	/// <summary>A string literal (kept), or one declarator of a <c>declare const</c> list with a literal initializer.</summary>
	[GeneratedRegex(Strings + """|(?<name>[A-Za-z_$][\w$]*)\s*=\s*""" + Literal + """(?=\s*[,;])""")]
	private static partial Regex Declarator();

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
