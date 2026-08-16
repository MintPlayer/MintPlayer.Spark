using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Reports a <c>[Breadcrumb]</c> template that names a property excluded by
/// <c>[IgnoreProperty]</c>.
/// <para>
/// The model synchronizer already fails on this, but only when someone runs
/// <c>--spark-synchronize-model</c> — which can be long after the attribute was added, and the
/// failure surfaces as a command that throws rather than as a problem with the code. Catching it
/// at build time puts the error on the two attributes that actually contradict each other.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IgnoredBreadcrumbFieldAnalyzer : DiagnosticAnalyzer
{
    private const string BreadcrumbAttributeFullName = "MintPlayer.Spark.Abstractions.BreadcrumbAttribute";

    internal static readonly DiagnosticDescriptor IgnoredBreadcrumbFieldRule = new(
        id: "SPARK003",
        title: "Breadcrumb template references an ignored property",
        messageFormat: "Breadcrumb template on '{0}' references '{{{1}}}', but '{1}' is marked [IgnoreProperty] and is not part of the model",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A breadcrumb placeholder must name a property that is part of the Spark model. "
            + "Remove the placeholder from the template, or drop [IgnoreProperty] from the property.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(IgnoredBreadcrumbFieldRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        var breadcrumb = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == BreadcrumbAttributeFullName);
        if (breadcrumb is null)
            return;

        if (breadcrumb.ConstructorArguments.Length == 0
            || breadcrumb.ConstructorArguments[0].Value is not string template)
            return;

        // Walk the whole base chain: the template may name an inherited property.
        var ignored = new HashSet<string>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsIgnoredForSparkModel())
                    ignored.Add(property.Name);
            }
        }

        if (ignored.Count == 0)
            return;

        var location = breadcrumb.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? type.Locations.FirstOrDefault()
            ?? Location.None;

        foreach (var field in ExtractPlaceholders(template))
        {
            if (ignored.Contains(field))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    IgnoredBreadcrumbFieldRule, location, type.Name, field));
            }
        }
    }

    /// <summary>
    /// Pulls the <c>{Field}</c> names out of a breadcrumb template. Deliberately conservative —
    /// only well-formed single-identifier placeholders are returned, so a malformed template
    /// produces no diagnostic here and is left to the synchronizer's template parser.
    /// </summary>
    private static IEnumerable<string> ExtractPlaceholders(string template)
    {
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0) yield break;

            var close = template.IndexOf('}', open + 1);
            if (close < 0) yield break;

            var name = template.Substring(open + 1, close - open - 1);
            if (name.Length > 0 && IsIdentifier(name))
                yield return name;

            index = close + 1;
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (!char.IsLetter(value[0]) && value[0] != '_')
            return false;

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }
}
