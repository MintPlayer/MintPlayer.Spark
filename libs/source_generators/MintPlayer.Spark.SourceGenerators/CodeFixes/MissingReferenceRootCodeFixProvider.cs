using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Naming;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MintPlayer.Spark.SourceGenerators.CodeFixes;

/// <summary>
/// SPARK056: adds the entity root the reference resolves through (<c>docs/default_index_PRD.md</c>, D8) —
/// <c>public IRavenQueryable&lt;T&gt; {Plural} =&gt; Session.Query&lt;T&gt;();</c> — to the context, written the way the
/// app contexts write theirs, so the generator classifies it as the plain root (SP3).
/// </summary>
/// <remarks>
/// A solution-level change: the reference sits on an entity and the root goes on the context, usually two documents.
/// The root takes <c>IndexNaming</c>'s plural, or the first free <c>{Plural}2</c>, … against the context's members
/// including inherited ones, the same rule as the generated view roots (D7). The root goes in the context's
/// hand-written part, never its generated one. IDE-only: <c>dotnet build</c> never loads a fix provider.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingReferenceRootCodeFixProvider)), Shared]
public sealed class MissingReferenceRootCodeFixProvider : CodeFixProvider
{
    private const string RavenLinqNamespace = "Raven.Client.Documents.Linq";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("SPARK056");

    // No Fix All: two references to one type would each add a root, and a batch fixer would need
    // Microsoft.CodeAnalysis.Features, a far heavier reference than this assembly carries.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(MissingReferenceRootAnalyzer.EntityTypeProperty, out var entityName) || entityName is null) continue;
            if (!diagnostic.Properties.TryGetValue(MissingReferenceRootAnalyzer.ContextTypeProperty, out var contextName) || contextName is null) continue;

            var plan = await PlanAsync(context.Document, entityName, contextName, context.CancellationToken).ConfigureAwait(false);
            if (plan is null) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add the root '{plan.Value.MemberName}' to '{plan.Value.ContextName}'",
                    _ => Task.FromResult(plan.Value.Solution),
                    equivalenceKey: $"{nameof(MissingReferenceRootCodeFixProvider)}.{entityName}"),
                diagnostic);
        }
    }

    private static async Task<(Solution Solution, string MemberName, string ContextName)?> PlanAsync(
        Document document, string entityName, string contextName, CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation?.GetTypeByMetadataName(entityName) is not { } entity) return null;
        if (compilation.GetTypeByMetadataName(contextName) is not { } contextType) return null;

        var solution = document.Project.Solution;

        // The hand-written part of the context: a fix that edited the generated part would vanish on the next build.
        var target = contextType.DeclaringSyntaxReferences
            .Select(r => (Reference: r, Document: solution.GetDocument(r.SyntaxTree)))
            .Where(x => x.Document is not null
                && !x.Reference.SyntaxTree.FilePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase))
            .Select(x => (Syntax: x.Reference.GetSyntax(cancellationToken) as ClassDeclarationSyntax, x.Document))
            .FirstOrDefault(x => x.Syntax is not null);
        if (target.Syntax is null || target.Document is null) return null;

        var memberName = FreeName(contextType, IndexNaming.Pluralize(entity.Name));

        var property = (MemberDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            $"public IRavenQueryable<{entity.Name}> {memberName} => Session.Query<{entity.Name}>();")!;
        property = property.WithAdditionalAnnotations(Formatter.Annotation);

        // After the context's last root, so the new one reads as one of them; otherwise first.
        var classDeclaration = target.Syntax;
        var lastRoot = classDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .LastOrDefault(p => p.Type is GenericNameSyntax { Identifier.ValueText: "IRavenQueryable" }
                || (p.Type is QualifiedNameSyntax { Right: GenericNameSyntax { Identifier.ValueText: "IRavenQueryable" } }));
        var members = classDeclaration.Members;
        var index = lastRoot is null ? 0 : members.IndexOf(lastRoot) + 1;
        var newClass = classDeclaration.WithMembers(members.Insert(index, property));

        var root = await target.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit) return null;

        unit = unit.ReplaceNode(classDeclaration, newClass);
        unit = AddUsing(unit, RavenLinqNamespace);

        var contextNamespace = contextType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var entityNamespace = entity.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        if (entityNamespace is not null
            && contextNamespace != entityNamespace
            && !contextNamespace.StartsWith(entityNamespace + ".", System.StringComparison.Ordinal))
            unit = AddUsing(unit, entityNamespace);

        return (solution.WithDocumentSyntaxRoot(target.Document.Id, unit), memberName, contextType.Name);
    }

    private static CompilationUnitSyntax AddUsing(CompilationUnitSyntax unit, string ns)
    {
        if (unit.Usings.Any(u => u.Alias is null && u.Name?.ToString() == ns)) return unit;

        var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        // Kept in the file's order: the new directive goes after the last one that sorts before it.
        var usings = unit.Usings;
        var insertAt = 0;
        while (insertAt < usings.Count
            && string.CompareOrdinal(usings[insertAt].Name?.ToString(), ns) < 0
            && usings[insertAt].StaticKeyword.IsKind(SyntaxKind.None)
            && usings[insertAt].Alias is null)
            insertAt++;

        return unit.WithUsings(usings.Insert(insertAt, directive));
    }

    /// <summary>The first of <paramref name="wanted"/>, <c>{wanted}2</c>, … that no member of the context or of a base type (private base members excepted) takes.</summary>
    private static string FreeName(INamedTypeSymbol contextType, string wanted)
    {
        var taken = new HashSet<string>(System.StringComparer.Ordinal) { contextType.Name };
        for (var current = contextType; current is not null; current = current.BaseType)
        {
            var isOwn = SymbolEqualityComparer.Default.Equals(current, contextType);
            foreach (var member in current.GetMembers())
            {
                if (!isOwn && member.DeclaredAccessibility == Accessibility.Private) continue;
                taken.Add(member.Name);
            }
        }

        var chosen = wanted;
        for (var suffix = 2; taken.Contains(chosen); suffix++)
            chosen = wanted + suffix;

        return chosen;
    }
}
