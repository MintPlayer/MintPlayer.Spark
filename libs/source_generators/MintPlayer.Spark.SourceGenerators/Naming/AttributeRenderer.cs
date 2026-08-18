using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MintPlayer.Spark.SourceGenerators.Naming;

/// <summary>
/// Renders an entity property's attributes as C# source so they can be copied onto the generated
/// index-entity property.
/// <para>
/// Carry-over is a <strong>deny-list</strong>: everything is copied except the generator's own directives.
/// The reference implementation whitelists instead, so any attribute a developer puts on a property outside
/// its list vanishes with no indication. Here the only things dropped are attributes whose arguments cannot
/// be rendered faithfully, and those are reported rather than discarded quietly.
/// </para>
/// </summary>
internal static class AttributeRenderer
{
    private const string SearchAttribute = "MintPlayer.Spark.Abstractions.SearchAttribute";
    private const string IgnoreForIndexAttribute = "MintPlayer.Spark.Abstractions.IgnoreForIndexAttribute";
    private const string GenerateIndexAttribute = "MintPlayer.Spark.Abstractions.GenerateIndexAttribute";
    private const string IgnorePropertyAttribute = "MintPlayer.Spark.Abstractions.IgnorePropertyAttribute";

    /// <summary>Reference-shaped attributes: copied to the field, never to its sort companion.</summary>
    private static readonly HashSet<string> ReferenceAttributes = new(System.StringComparer.Ordinal)
    {
        "MintPlayer.Spark.Abstractions.ReferenceAttribute",
        "MintPlayer.Spark.Abstractions.LookupReferenceAttribute",
    };

    /// <summary>
    /// Directives that instruct this generator rather than describing the property. Copying them would be
    /// meaningless on the index entity, and copying <c>[IgnoreProperty]</c> would silently remove the field
    /// from the model.
    /// </summary>
    private static readonly HashSet<string> Directives = new(System.StringComparer.Ordinal)
    {
        SearchAttribute,
        IgnoreForIndexAttribute,
        GenerateIndexAttribute,
        IgnorePropertyAttribute,
    };

    /// <summary>
    /// Attributes to copy onto the index-entity property, plus the names of any that could not be rendered.
    /// </summary>
    public static (List<string> Rendered, List<string> Unrenderable) ForField(IPropertySymbol property)
        => Render(property, includeReferences: true);

    /// <summary>
    /// Attributes to copy onto the sort companion: the same set minus the reference-shaped ones. A companion
    /// is a plain sort key, so declaring it a reference would make the model resolve a second reference to
    /// the same target.
    /// </summary>
    public static List<string> ForSortCompanion(IPropertySymbol property)
        => Render(property, includeReferences: false).Rendered;

    private static (List<string> Rendered, List<string> Unrenderable) Render(
        IPropertySymbol property,
        bool includeReferences)
    {
        var rendered = new List<string>();
        var unrenderable = new List<string>();

        foreach (var attribute in property.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (name is null) continue;
            if (Directives.Contains(name)) continue;
            if (!includeReferences && ReferenceAttributes.Contains(name)) continue;

            if (TryRender(attribute, out var text))
                rendered.Add(text!);
            else
                unrenderable.Add(attribute.AttributeClass!.Name);
        }

        return (rendered, unrenderable);
    }

    private static bool TryRender(AttributeData attribute, out string? text)
    {
        text = null;

        // An attribute whose type or constructor does not resolve in this compilation is an ERROR symbol:
        // its name renders without a namespace and its ConstructorArguments come back empty. Rendering it
        // anyway produced `[MaxLength]` from `[MaxLength(250)]` -- valid-looking, silently wrong source. So
        // an unresolved attribute is refused here and reported instead.
        if (attribute.AttributeClass is not { } attributeClass) return false;
        if (attributeClass.TypeKind == TypeKind.Error) return false;
        if (attribute.AttributeConstructor is null) return false;

        var name = attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var positional = new List<string>();
        foreach (var argument in attribute.ConstructorArguments)
        {
            if (!TryRenderConstant(argument, out var value)) return false;
            positional.Add(value!);
        }

        // Trailing arguments that merely restate an optional parameter's default add noise -- an unadorned
        // [Reference(typeof(Company))] otherwise renders as [Reference(typeof(Company), null)].
        var parameters = attribute.AttributeConstructor.Parameters;
        while (positional.Count > 0 && positional.Count <= parameters.Length)
        {
            var index = positional.Count - 1;
            var parameter = parameters[index];
            if (!parameter.HasExplicitDefaultValue) break;
            if (!TryRenderPrimitiveOrNull(parameter.ExplicitDefaultValue, out var defaultText)) break;
            if (!string.Equals(positional[index], defaultText, System.StringComparison.Ordinal)) break;
            positional.RemoveAt(index);
        }

        var arguments = new List<string>(positional);
        foreach (var argument in attribute.NamedArguments)
        {
            if (!TryRenderConstant(argument.Value, out var value)) return false;
            arguments.Add($"{argument.Key} = {value}");
        }

        text = arguments.Count == 0
            ? $"[{name}]"
            : $"[{name}({string.Join(", ", arguments)})]";
        return true;
    }

    private static bool TryRenderPrimitiveOrNull(object? value, out string? text)
    {
        if (value is null)
        {
            text = "null";
            return true;
        }

        return TryRenderPrimitive(value, out text);
    }

    private static bool TryRenderConstant(TypedConstant constant, out string? text)
    {
        text = null;

        if (constant.IsNull)
        {
            text = "null";
            return true;
        }

        switch (constant.Kind)
        {
            case TypedConstantKind.Type:
                if (constant.Value is not ITypeSymbol type) return false;
                text = $"typeof({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";
                return true;

            case TypedConstantKind.Enum:
                // Rendered as a cast rather than a member name: a flags combination has no single member.
                if (constant.Type is null) return false;
                text = $"({constant.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){constant.Value}";
                return true;

            case TypedConstantKind.Primitive:
                return TryRenderPrimitive(constant.Value, out text);

            case TypedConstantKind.Array:
                var elements = new List<string>();
                foreach (var element in constant.Values)
                {
                    if (!TryRenderConstant(element, out var rendered)) return false;
                    elements.Add(rendered!);
                }

                var elementType = (constant.Type as IArrayTypeSymbol)?.ElementType
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (elementType is null) return false;
                text = $"new {elementType}[] {{ {string.Join(", ", elements)} }}";
                return true;

            default:
                return false;
        }
    }

    private static bool TryRenderPrimitive(object? value, out string? text)
    {
        switch (value)
        {
            case string s:
                text = Quote(s);
                return true;
            case bool b:
                text = b ? "true" : "false";
                return true;
            case char c:
                text = $"'{(c == '\'' ? "\\'" : c == '\\' ? "\\\\" : c.ToString())}'";
                return true;
            case float f:
                text = $"{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}f";
                return true;
            case double d:
                text = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case decimal m:
                text = $"{m.ToString(System.Globalization.CultureInfo.InvariantCulture)}m";
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                text = System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
