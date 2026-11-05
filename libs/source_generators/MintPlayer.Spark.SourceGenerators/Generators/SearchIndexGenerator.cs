using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits the application's <c>Spark_Search</c> index (<c>docs/spark_search_PRD.md</c> D5): one map per entity type
/// whose composed model has <c>"search": { "enabled": true }</c>, into the shape of
/// <c>MintPlayer.Spark.Abstractions.Search.SparkSearchEntry</c>. Only for an application that references
/// MintPlayer.Spark.Search, and the index lives in the application like every other RavenDB index.
/// </summary>
/// <remarks>
/// The searched text is the placeholders of the type's <c>search.label</c> (else of its <c>breadcrumb</c>), then its
/// <c>search.fields</c>. A field is used when it is a readable public property of the CLR type holding a string or a
/// collection of strings; anything else (a reference id, a number) is left out, because its text is not what a visitor
/// types. Every identifier in the emitted source comes from a resolved symbol, never from the JSON text (R2-C5).
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class SearchIndexGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var appDataDirProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) => SparkAppDataDir.Read(options.GlobalOptions));

        var appModelFilesProvider = context.AdditionalTextsProvider
            .Combine(appDataDirProvider)
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static p =>
            {
                var file = Path.GetFileName(p.Left.Left.Path);
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (p.Right.GetOptions(p.Left.Left).TryGetValue(LibraryLayersGenerator.PathMetadata, out var layerPath)
                    && !string.IsNullOrWhiteSpace(layerPath))
                    return false;
                return SparkAppDataDir.Contains(p.Left.Left.Path, p.Left.Right, "Model");
            })
            .Select(static (p, ct) => new AppModelFileInfo
            {
                Path = p.Left.Left.Path,
                Text = p.Left.Left.GetText(ct)?.ToString() ?? string.Empty,
            })
            .Collect();

        var producer = appModelFilesProvider
            .Combine(context.CompilationProvider)
            .Combine(settingsProvider)
            .Select(static (p, ct) =>
            {
                var compilation = p.Left.Right;
                var entries = new List<SearchMapInfo>();
                var isApplication = compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;
                if (isApplication
                    && compilation.GetTypeByMetadataName("MintPlayer.Spark.Search.SparkSearchExtensions") is not null
                    && compilation.GetTypeByMetadataName("MintPlayer.Spark.Abstractions.Search.SparkSearchEntry") is not null
                    && compilation.GetTypeByMetadataName("MintPlayer.Spark.SparkMultiMapIndexCreationTask`1") is not null)
                {
                    foreach (var json in ComposedModel.Compose(ComposedModel.LibraryFiles(compilation), p.Left.Left))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Read(json, compilation) is { } entry)
                            entries.Add(entry);
                    }
                }
                return (Producer)new SearchIndexProducer(
                    entries.OrderBy(e => e.EntityName, StringComparer.Ordinal).ToList(),
                    p.Right.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(producer);
    }

    private static SearchMapInfo? Read(string json, Compilation compilation)
    {
        SparkJsonObject root;
        try
        {
            if (SparkJson.Parse(json) is not SparkJsonObject parsed)
                return null;
            root = parsed;
        }
        catch (SparkJsonException)
        {
            return null;
        }

        if (root["persistentObject"] is not SparkJsonObject po
            || po["search"] is not SparkJsonObject search
            || search["enabled"] is not SparkJsonBoolean { Value: true }
            || po["name"] is not SparkJsonString { Value: { Length: > 0 } name }
            || po["clrType"] is not SparkJsonString { Value: { Length: > 0 } clrType })
            return null;

        if (compilation.GetTypeByMetadataName(clrType) is not { } type)
            return null;

        // The label's attributes always: a label lookup (resolve) finds its object through this text. Then search.fields.
        IEnumerable<string> names;
        if (search["label"] is SparkJsonString { Value: { Length: > 0 } label })
            names = Placeholders(label);
        else if (po["breadcrumb"] is SparkJsonString { Value: { Length: > 0 } breadcrumb })
            names = Placeholders(breadcrumb);
        else
            names = Array.Empty<string>();
        if (search["fields"] is SparkJsonArray fields)
            names = names.Concat(fields.Items.OfType<SparkJsonString>().Select(s => s.Value));

        var textFields = new List<SearchFieldInfo>();
        foreach (var fieldName in names.Distinct(StringComparer.Ordinal))
        {
            if (FindProperty(type, fieldName) is not { } property)
                continue;
            if (property.Type.SpecialType == SpecialType.System_String)
                textFields.Add(new SearchFieldInfo { Name = property.Name, IsCollection = false });
            else if (IsStringCollection(property.Type))
                textFields.Add(new SearchFieldInfo { Name = property.Name, IsCollection = true });
        }
        if (textFields.Count == 0)
            return null;

        var isDeleted = FindProperty(type, "IsDeleted") is { } deleted
            && (deleted.Type.SpecialType == SpecialType.System_Boolean
                || deleted.Type is INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable
                    && nullable.TypeArguments[0].SpecialType == SpecialType.System_Boolean);

        return new SearchMapInfo
        {
            EntityName = name,
            EntityFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Fields = textFields,
            HasIsDeleted = isDeleted,
        };
    }

    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    private static IEnumerable<string> Placeholders(string template)
        => Placeholder.Matches(template).Cast<Match>().Select(m => m.Groups[1].Value);

    private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } property)
                    return property;
            }
        }
        return null;
    }

    private static bool IsStringCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String })
            return true;
        return type is INamedTypeSymbol named
            && named.AllInterfaces.Concat(named.TypeKind == TypeKind.Interface ? new[] { named } : Array.Empty<INamedTypeSymbol>())
                .Any(i => i.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                    && i.TypeArguments[0].SpecialType == SpecialType.System_String);
    }
}
