using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits a transient lookup reference class for every <c>[SparkLookup]</c> enum (commerce X20 item 5, G1).
/// <para>
/// The labels are <c>translations.json</c> keys, <c>lookup.{Enum}.{Member}</c>, never inline text: the item's
/// <c>Values</c> is <c>TranslatedString.FromKey(...)</c>, which <c>LookupReferenceService</c> resolves through
/// the composed translation layers when it serves the lookup. That replaces the hand-written lookup classes
/// whose labels restated every member in <c>_TS(...)</c>, with the texts now where the rest of an app's texts
/// are. <c>tools/verify-translation-keys.test.mjs</c> guards that every key exists and that no <c>lookup.*</c>
/// key outlives its member.
/// </para>
/// <para>
/// Runs in every project that references <c>MintPlayer.Spark.Model</c> (where the attribute and
/// <c>TransientLookupReference&lt;TKey&gt;</c> live): a lookup belongs to the project that declares it.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public class SparkLookupGenerator : IncrementalGenerator
{
    internal const string SparkLookupAttributeFullName = "MintPlayer.Spark.Abstractions.SparkLookupAttribute";
    private const string SparkLookupKeyAttributeFullName = "MintPlayer.Spark.Abstractions.SparkLookupKeyAttribute";
    private const string SparkLookupOrderAttributeFullName = "MintPlayer.Spark.Abstractions.SparkLookupOrderAttribute";
    private const string TransientLookupReferenceMetadataName = "MintPlayer.Spark.Abstractions.TransientLookupReference`1";

    /// <summary>The members the generated class (or its base) already declares: a string-keyed constant cannot take these names.</summary>
    private static readonly HashSet<string> ReservedMemberNames = new(System.StringComparer.Ordinal)
    {
        "Items", "DisplayType", "Key", "Values", "Description", "Create", "Initialize",
    };

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var lookupsProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SparkLookupAttributeFullName,
                predicate: static (node, _) => node is EnumDeclarationSyntax,
                transform: static (ctx, ct) => Describe((INamedTypeSymbol)ctx.TargetSymbol, ctx.Attributes[0]))
            .Collect();

        var knowsLookupsProvider = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(TransientLookupReferenceMetadataName) is not null);

        var producerProvider = lookupsProvider
            .Combine(knowsLookupsProvider)
            .Combine(settingsProvider)
            .Select(static Producer (providers, _) => new SparkLookupProducer(
                providers.Left.Left.ToList(),
                providers.Left.Right,
                providers.Right.RootNamespace ?? "GeneratedCode"));

        context.ProduceCode(producerProvider);
        context.ReportDiagnostics(
            producerProvider.Select(static IDiagnosticReporter (producer, _) => (IDiagnosticReporter)producer));
    }

    private static SparkLookupInfo Describe(INamedTypeSymbol enumSymbol, AttributeData attribute)
    {
        var explicitName = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        var displayType = "Dropdown";
        var stringKeys = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "DisplayType" && named.Value.Value is int displayValue)
                displayType = displayValue == 1 ? "Modal" : "Dropdown";
            else if (named.Key == "StringKeys" && named.Value.Value is bool flag)
                stringKeys = flag;
        }

        var info = new SparkLookupInfo
        {
            Namespace = enumSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
            EnumName = enumSymbol.Name,
            EnumFullName = enumSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ClassName = explicitName ?? DefaultName(enumSymbol.Name),
            Accessibility = enumSymbol.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ? "public" : "internal",
            DisplayType = displayType,
            StringKeys = stringKeys,
            Location = enumSymbol.Locations.FirstOrDefault(l => l.IsInSource).AsKey(),
        };

        string? keyedMember = null;
        foreach (var field in enumSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
        {
            var keyAttribute = field.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SparkLookupKeyAttributeFullName);
            var storedKey = keyAttribute?.ConstructorArguments.FirstOrDefault().Value as string;
            if (keyAttribute is not null) keyedMember ??= field.Name;
            var order = field.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SparkLookupOrderAttributeFullName)?
                .ConstructorArguments.FirstOrDefault().Value as int? ?? 0;

            info.Members.Add(new SparkLookupMemberInfo
            {
                Name = field.Name,
                StringKey = storedKey ?? field.Name,
                Order = order,
            });
        }

        // The display order: by [SparkLookupOrder], declaration order within one number (OrderBy is stable).
        info.Members = info.Members.OrderBy(m => m.Order).ToList();

        info.Error = Validate(enumSymbol, info, keyedMember);

        // An alias (B = A) is one value under two names: the wire key is the value's ToString(), so the two
        // items would arrive with one key.
        if (info.Error is null && !info.StringKeys
            && enumSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue)
                .GroupBy(f => f.ConstantValue).FirstOrDefault(g => g.Count() > 1) is { } alias)
            info.Error = $"members {string.Join(" and ", alias.Select(f => $"'{f.Name}'"))} share one value";

        return info;
    }

    /// <summary><c>ECarStatus</c> → <c>CarStatus</c>; any other name is kept (and then collides with the enum, see <see cref="Validate"/>).</summary>
    private static string DefaultName(string enumName)
        => enumName.Length > 1 && enumName[0] == 'E' && char.IsUpper(enumName[1]) ? enumName.Substring(1) : enumName;

    private static string? Validate(INamedTypeSymbol enumSymbol, SparkLookupInfo info, string? keyedMember)
    {
        if (enumSymbol.ContainingType is not null)
            return "it is nested in a type; declare it at namespace level";
        if (!SyntaxFacts.IsValidIdentifier(info.ClassName) || SyntaxFacts.GetKeywordKind(info.ClassName) != SyntaxKind.None)
            return $"its lookup name '{info.ClassName}' is not a valid C# class name";
        if (info.ClassName == info.EnumName)
            return $"its lookup name '{info.ClassName}' is the enum's own name; prefix the enum with 'E' or pass a name: [SparkLookup(\"{info.EnumName}Lookup\")]";
        if (info.Members.Count == 0)
            return "it has no members";
        if (!info.StringKeys && keyedMember is not null)
            return $"member '{keyedMember}' has [SparkLookupKey], which only a StringKeys lookup uses";
        if (info.StringKeys)
        {
            if (info.Members.FirstOrDefault(m => ReservedMemberNames.Contains(m.Name) || m.Name == info.ClassName) is { } reserved)
                return $"member '{reserved.Name}' would collide with a member of the generated class";
            if (info.Members.GroupBy(m => m.StringKey, System.StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                return $"two members share the key '{duplicate.Key}'";
            if (info.Members.FirstOrDefault(m => string.IsNullOrEmpty(m.StringKey)) is { } empty)
                return $"member '{empty.Name}' has an empty [SparkLookupKey]";
        }
        return null;
    }
}
