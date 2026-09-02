using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Turns a property's documentation comment into the plain text a tooltip can show (#348).
/// </summary>
/// <remarks>
/// <para>
/// Two inputs, one renderer. When the compilation parses documentation (<c>GenerateDocumentationFile</c>
/// on), <see cref="ISymbol.GetDocumentationCommentXml"/> returns a <c>&lt;member&gt;</c> element with
/// resolved crefs (<c>P:Ns.Type.Prop</c>). When it does not — the default for every project in this
/// repository — Roslyn lexes <c>///</c> lines as ordinary <see cref="SyntaxKind.SingleLineCommentTrivia"/>,
/// so the text is rebuilt from the property's leading trivia. Both land in <see cref="Render"/>.
/// </para>
/// <para>
/// Rendering rules: <c>&lt;para&gt;</c> and <c>&lt;br/&gt;</c> break lines; <c>&lt;see cref/&gt;</c> becomes the
/// simple member name (<c>T:Fx.Company</c> → <c>Company</c>, <c>Company.Name</c> → <c>Name</c>,
/// <c>Box`1</c> → <c>Box</c>); <c>&lt;see langword/&gt;</c>, <c>&lt;paramref/&gt;</c> and
/// <c>&lt;typeparamref/&gt;</c> become their name; <c>&lt;c&gt;</c>/<c>&lt;code&gt;</c> keep their text; other
/// tags are unwrapped. Whitespace collapses within a line; empty lines are dropped. A comment
/// without a <c>&lt;summary&gt;</c> (<c>&lt;inheritdoc/&gt;</c>, remarks only) yields <see langword="null"/>.
/// </para>
/// </remarks>
internal static class XmlDocSummary
{
    private static readonly Regex SummaryFallback =
        new(@"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Summary for a property, from structured XML if the compilation has it, else from trivia.</summary>
    public static string? For(IPropertySymbol symbol, SyntaxNode declaration, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(xml))
            return FromXml(xml!);

        var fromTrivia = FromTrivia(declaration.GetLeadingTrivia());
        return fromTrivia is null ? null : FromXml("<member>" + fromTrivia + "</member>");
    }

    /// <summary>Joins the <c>///</c> lines of the trivia into one XML fragment, or <see langword="null"/> when there are none.</summary>
    public static string? FromTrivia(SyntaxTriviaList trivia)
    {
        var builder = new StringBuilder();
        foreach (var t in trivia)
        {
            if (t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                // Structured doc trivia (DocumentationMode >= Parse) still carries the `///` prefixes
                // in its full text; strip them line by line like the plain case.
                AppendDocLines(builder, t.ToFullString());
            }
            else if (t.IsKind(SyntaxKind.SingleLineCommentTrivia))
            {
                var text = t.ToString();
                if (text.StartsWith("///", StringComparison.Ordinal) && !text.StartsWith("////", StringComparison.Ordinal))
                    AppendDocLines(builder, text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void AppendDocLines(StringBuilder builder, string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart(' ', '\t');
            if (line.StartsWith("///", StringComparison.Ordinal))
                line = line.Substring(3);
            if (line.StartsWith(" ", StringComparison.Ordinal))
                line = line.Substring(1);
            builder.Append(line).Append('\n');
        }
    }

    /// <summary>Renders the <c>&lt;summary&gt;</c> of a <c>&lt;member&gt;</c> (or bare) XML fragment to plain text.</summary>
    public static string? FromXml(string xml)
    {
        XElement? summary;
        try
        {
            var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            summary = root.Name.LocalName == "summary"
                ? root
                : root.Descendants("summary").FirstOrDefault();
        }
        catch (System.Xml.XmlException)
        {
            // Malformed XML (an unescaped `<` in prose, an unclosed tag): salvage the summary text
            // rather than losing the description outright.
            var match = SummaryFallback.Match(xml);
            if (!match.Success)
                return null;
            return Normalize(Regex.Replace(match.Groups[1].Value, "<[^>]*>", string.Empty));
        }

        if (summary is null)
            return null;

        var builder = new StringBuilder();
        Render(summary, builder);
        return Normalize(builder.ToString());
    }

    private static void Render(XElement element, StringBuilder builder)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    // Source-line wrapping inside a text node is not a line break the reader
                    // should see; only <para> and <br/> are. Collapse it to a space here so the
                    // per-line normalization below never sees it.
                    builder.Append(Regex.Replace(text.Value, @"\s+", " "));
                    break;

                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "para":
                            builder.Append('\n');
                            Render(child, builder);
                            builder.Append('\n');
                            break;
                        case "br":
                            builder.Append('\n');
                            break;
                        case "see":
                        case "seealso":
                            var cref = (string?)child.Attribute("cref");
                            var langword = (string?)child.Attribute("langword");
                            var href = (string?)child.Attribute("href");
                            if (!child.IsEmpty && child.Nodes().Any())
                                Render(child, builder);
                            else if (cref is not null)
                                builder.Append(SimpleName(cref));
                            else if (langword is not null)
                                builder.Append(langword);
                            else if (href is not null)
                                builder.Append(href);
                            break;
                        case "paramref":
                        case "typeparamref":
                            builder.Append((string?)child.Attribute("name") ?? string.Empty);
                            break;
                        default:
                            Render(child, builder);
                            break;
                    }
                    break;
            }
        }
    }

    /// <summary><c>P:Fx.Company.Name</c> → <c>Name</c>; <c>T:Fx.Box`1</c> → <c>Box</c>; <c>M:Fx.A.Do(System.Int32)</c> → <c>Do</c>; <c>Company.Name</c> → <c>Name</c>.</summary>
    internal static string SimpleName(string cref)
    {
        var text = cref.Trim();
        if (text.Length > 2 && text[1] == ':')
            text = text.Substring(2);

        var paren = text.IndexOf('(');
        if (paren >= 0)
            text = text.Substring(0, paren);

        var generic = text.IndexOfAny(['`', '<', '{']);
        if (generic >= 0)
            text = text.Substring(0, generic);

        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text.Substring(dot + 1) : text;
    }

    /// <summary>Collapses whitespace within lines, drops empty lines, joins with <c>\n</c>; <see langword="null"/> when nothing is left.</summary>
    private static string? Normalize(string text)
    {
        var lines = text.Split('\n')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => l.Length > 0);
        var result = string.Join("\n", lines);
        return result.Length == 0 ? null : result;
    }
}
