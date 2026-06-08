namespace MintPlayer.Spark.Services.Breadcrumb;

/// <summary>A parsed segment of a breadcrumb template.</summary>
public abstract record BreadcrumbToken;

/// <summary>Literal text emitted verbatim.</summary>
public sealed record LiteralToken(string Text) : BreadcrumbToken;

/// <summary>A <c>{AttributeName}</c> placeholder referring to an attribute of the current entity.</summary>
public sealed record FieldToken(string AttributeName) : BreadcrumbToken;

/// <summary>
/// Parser for breadcrumb templates. Grammar: <c>(literal | '{' attributeName '}')*</c>,
/// with <c>{{</c> / <c>}}</c> escaping literal braces. The single source of truth for how a
/// <c>[Breadcrumb]</c> / <see cref="Abstractions.EntityTypeDefinition.Breadcrumb"/> string is
/// decomposed — used by model-sync validation and (later) the runtime resolver.
/// </summary>
public static class BreadcrumbTemplate
{
    /// <summary>
    /// Parses <paramref name="template"/> into tokens, merging adjacent literals.
    /// </summary>
    /// <exception cref="FormatException">Unbalanced or empty <c>{}</c> placeholder.</exception>
    public static IReadOnlyList<BreadcrumbToken> Parse(string template)
    {
        if (string.IsNullOrEmpty(template))
            return [];

        var tokens = new List<BreadcrumbToken>();
        var literal = new System.Text.StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                tokens.Add(new LiteralToken(literal.ToString()));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];

            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }

                // Start of a placeholder — read up to the closing '}'.
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new FormatException($"Unterminated '{{' in breadcrumb template \"{template}\".");

                var name = template[(i + 1)..close];
                if (string.IsNullOrWhiteSpace(name))
                    throw new FormatException($"Empty placeholder '{{}}' in breadcrumb template \"{template}\".");
                if (name.Contains('{'))
                    throw new FormatException($"Nested '{{' inside a placeholder in breadcrumb template \"{template}\".");

                FlushLiteral();
                tokens.Add(new FieldToken(name.Trim()));
                i = close + 1;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }
                throw new FormatException($"Unescaped '}}' in breadcrumb template \"{template}\". Use '}}}}' for a literal brace.");
            }

            literal.Append(c);
            i++;
        }

        FlushLiteral();
        return tokens;
    }

    /// <summary>The distinct attribute names referenced by <c>{…}</c> placeholders.</summary>
    public static IEnumerable<string> FieldNames(string template)
        => Parse(template).OfType<FieldToken>().Select(t => t.AttributeName).Distinct(StringComparer.Ordinal);
}
