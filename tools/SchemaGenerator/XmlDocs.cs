using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MintPlayer.Spark.SchemaGenerator;

/// <summary>
/// Reads the <c>&lt;summary&gt;</c> of a type or member from the XML documentation file the compiler
/// writes next to its assembly (<c>GenerateDocumentationFile</c>), as plain text for a schema's
/// <c>description</c>, which editors show on hover.
/// </summary>
/// <remarks>
/// Only Spark's own assemblies are read. The framework's documentation lives in the reference packs,
/// not next to the runtime assemblies, and whether it is found would depend on the machine, while the
/// output must be byte-for-byte deterministic (see <see cref="SparkSchemaGenerator"/>). An assembly
/// without its XML file yields no descriptions rather than an error.
/// </remarks>
internal static partial class XmlDocs
{
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>> Files = new();

    // Paragraph and line breaks survive whitespace normalisation as these two placeholders.
    private const char ParagraphBreak = '\u0001';
    private const char LineBreak = '\u0002';

    /// <summary>The summary of <paramref name="member"/> (a type, property or field), or null when it has none.</summary>
    public static string? Summary(MemberInfo member)
    {
        var id = Id(member);
        if (id is null)
            return null;
        var type = member as Type ?? member.DeclaringType;
        return type is null ? null : Summary(type.Assembly, id, depth: 0);
    }

    private static string? Summary(Assembly assembly, string id, int depth)
    {
        if (depth > 4 || !Members(assembly).TryGetValue(id, out var element))
            return null;

        var summary = element.Element("summary");
        if (summary is null)
        {
            // <inheritdoc cref="..."/> is written to the XML file as it stands; resolve it here.
            var inherit = element.Element("inheritdoc")?.Attribute("cref")?.Value;
            return inherit is null ? null : Summary(assembly, inherit, depth + 1);
        }

        var builder = new StringBuilder();
        Render(summary, builder, assembly, depth);
        return Normalize(builder.ToString());
    }

    private static IReadOnlyDictionary<string, XElement> Members(Assembly assembly) => Files.GetOrAdd(assembly, static a =>
    {
        var empty = new Dictionary<string, XElement>(StringComparer.Ordinal);
        if (a.GetName().Name is not { } name || !name.StartsWith("MintPlayer.Spark", StringComparison.Ordinal) || string.IsNullOrEmpty(a.Location))
            return empty;
        var path = Path.ChangeExtension(a.Location, ".xml");
        if (!File.Exists(path))
            return empty;

        var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var member in XDocument.Load(path).Root?.Element("members")?.Elements("member") ?? [])
        {
            if (member.Attribute("name")?.Value is { } key)
                members[key] = member;
        }
        return members;
    });

    /// <summary>The documentation id of a type (<c>T:</c>), property (<c>P:</c>) or field (<c>F:</c>).</summary>
    private static string? Id(MemberInfo member) => member switch
    {
        Type type => "T:" + TypeName(type),
        PropertyInfo property when property.DeclaringType is { } declaring && property.GetIndexParameters().Length == 0
            => $"P:{TypeName(declaring)}.{property.Name}",
        FieldInfo field when field.DeclaringType is { } declaring => $"F:{TypeName(declaring)}.{field.Name}",
        _ => null,
    };

    /// <summary><c>Namespace.Outer.Inner</c>; a generic type by its definition, <c>Name`1</c>.</summary>
    private static string TypeName(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            type = type.GetGenericTypeDefinition();
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static void Render(XElement element, StringBuilder builder, Assembly assembly, int depth)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    RenderElement(child, builder, assembly, depth);
                    break;
            }
        }
    }

    private static void RenderElement(XElement element, StringBuilder builder, Assembly assembly, int depth)
    {
        switch (element.Name.LocalName)
        {
            case "see" or "seealso":
                if (!element.IsEmpty && element.Value.Trim().Length > 0)
                    Render(element, builder, assembly, depth);
                else if (element.Attribute("cref")?.Value is { } cref)
                    builder.Append(CrefText(cref));
                else if (element.Attribute("langword")?.Value is { } langword)
                    builder.Append(langword);
                else if (element.Attribute("href")?.Value is { } href)
                    builder.Append(href);
                break;
            case "paramref" or "typeparamref":
                builder.Append(element.Attribute("name")?.Value);
                break;
            case "para":
                builder.Append(ParagraphBreak);
                Render(element, builder, assembly, depth);
                builder.Append(ParagraphBreak);
                break;
            case "item":
                builder.Append(LineBreak).Append("- ");
                Render(element, builder, assembly, depth);
                break;
            case "list":
                Render(element, builder, assembly, depth);
                builder.Append(ParagraphBreak);
                break;
            case "br":
                builder.Append(LineBreak);
                break;
            case "inheritdoc":
                if (element.Attribute("cref")?.Value is { } inherited && Summary(assembly, inherited, depth + 1) is { } text)
                    builder.Append(text);
                break;
            default:
                // <c>, <code>, <b>, <i>, <em>, <term>, <description>, …: the text, without the markup.
                Render(element, builder, assembly, depth);
                break;
        }
    }

    /// <summary>
    /// The short text of a cref: its last segment, without generic arity or parameters. A property is
    /// camel-cased, as the App_Data files spell it (<c>P:…SparkQuery.SelectionMode</c> → <c>selectionMode</c>).
    /// </summary>
    private static string CrefText(string cref)
    {
        var kind = cref.Length > 1 && cref[1] == ':' ? cref[0] : '\0';
        var name = kind == '\0' ? cref : cref[2..];
        var parameters = name.IndexOf('(');
        if (parameters >= 0)
            name = name[..parameters];
        name = name[(name.LastIndexOf('.') + 1)..];
        var arity = name.IndexOf('`');
        if (arity >= 0)
            name = name[..arity];
        if (kind == 'P' && name.Length > 0)
            name = char.ToLowerInvariant(name[0]) + name[1..];
        return name;
    }

    private static string? Normalize(string text)
    {
        text = Whitespace().Replace(text, " ");
        text = BreakRun().Replace(text, match => match.Value.Contains(ParagraphBreak) ? "\n\n" : "\n");
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A run of breaks with the spaces around them collapses to the strongest break in it.
    [GeneratedRegex("[ \u0001\u0002]*[\u0001\u0002][ \u0001\u0002]*")]
    private static partial Regex BreakRun();
}
