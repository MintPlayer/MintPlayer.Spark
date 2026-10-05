using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Declares the members of the framework-stamped contracts — <c>IAuditCreated</c> / <c>IAuditModified</c>
/// (so <c>IAuditable</c>), <c>ISoftDeletable</c>, <c>IModeratable</c> — that a <c>partial</c> type leaves
/// out (#271). The interface is the opt-in; the members are boilerplate.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Triggered by the interface, not an attribute: the interface is already what the runtime keys on,
/// and a library without this generator then fails loudly (CS0535) instead of silently.</item>
/// <item>Only the missing members are emitted; a member the author declared (on the type, a base type, or
/// as an explicit implementation) is left alone, and a contract a base type already implements is skipped.</item>
/// <item>Every emitted member is <c>[ReadOnly(true)]</c>: the framework stamps it, so the model synchronizer
/// creates it read-only and edit-page-only.</item>
/// <item>The user-id members are <c>[Reference(typeof(SparkUser))]</c> when the compilation can see
/// <c>SparkUser</c>; otherwise they are plain strings and SPARK040 says why.</item>
/// </list>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class AuditMembersGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var typesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is TypeDeclarationSyntax { BaseList: not null }
                    and (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax),
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, ct) => x!)
            .Collect()
            .Select(static (found, ct) => Distinct(found));

        // A compilation-level fact, computed once rather than per type: whether the user-id members can
        // be references to SparkUser. A generator package never brings that assembly in (it contributes
        // no compile references), so this is purely what the library itself references.
        var canReferenceProvider = context.CompilationProvider
            .Select(static (compilation, ct) =>
                compilation.GetTypeByMetadataName(AuditContracts.SparkUser) is not null
                && compilation.GetTypeByMetadataName(AuditContracts.ReferenceAttribute) is not null);

        var inputs = typesProvider.Combine(canReferenceProvider);

        var sourceProvider = inputs
            .Combine(settingsProvider)
            .Select(static Producer (p, ct) => new AuditMembersProducer(p.Left.Left, p.Left.Right, p.Right.RootNamespace ?? "GeneratedCode"));

        var diagnosticsProvider = inputs
            .Select(static IDiagnosticReporter (p, ct) => new AuditMembersReporter(p.Left, p.Right));

        context.ProduceCode(sourceProvider);
        context.ReportDiagnostics(diagnosticsProvider);
    }

    private static AuditTypeInfo? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type)
            return null;

        var inherited = type.BaseType?.AllInterfaces
            .Select(static i => i.OriginalDefinition.ToDisplayString())
            .ToImmutableHashSet() ?? ImmutableHashSet<string>.Empty;

        var implemented = type.AllInterfaces
            .Select(static i => i.OriginalDefinition.ToDisplayString())
            .Where(name => !inherited.Contains(name))
            .ToImmutableHashSet();

        var missing = new List<string>();
        foreach (var contract in AuditContracts.All)
        {
            if (!implemented.Contains(contract.InterfaceName))
                continue;

            foreach (var member in contract.Members)
            {
                if (!IsDeclared(type, member.Name) && !missing.Contains(member.Name))
                    missing.Add(member.Name);
            }
        }

        if (missing.Count == 0)
            return null;

        return new AuditTypeInfo
        {
            FullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name = type.Name,
            Header = PartialTypeHeader.For(type),
            PathSpec = type.GetPathSpec(ct),
            IsPartial = PartialTypeHeader.IsPartialAllTheWayUp(type, ct),
            MissingMembers = string.Join(",", missing),
            Location = (ctx.Node as TypeDeclarationSyntax)?.Identifier.GetLocation().AsKey(),
        };
    }

    /// <summary>
    /// Whether <paramref name="name"/> is already a member of <paramref name="type"/>: declared on it or a
    /// base type (any kind — a field of that name would clash with the emitted property too), or
    /// implemented explicitly.
    /// </summary>
    private static bool IsDeclared(INamedTypeSymbol type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (!t.GetMembers(name).IsEmpty)
                return true;
        }

        return type.GetMembers()
            .OfType<IPropertySymbol>()
            .Any(p => p.ExplicitInterfaceImplementations.Any(i => i.Name == name));
    }

    /// <summary>One entry per type (a partial type arrives once per declaration with a base list), ordered for a stable file.</summary>
    private static ImmutableArray<AuditTypeInfo> Distinct(IEnumerable<AuditTypeInfo> found)
    {
        var seen = new Dictionary<string, AuditTypeInfo>(StringComparer.Ordinal);
        foreach (var info in found)
        {
            if (!seen.ContainsKey(info.FullyQualifiedName))
                seen.Add(info.FullyQualifiedName, info);
        }

        return [.. seen.Values.OrderBy(static x => x.FullyQualifiedName, StringComparer.Ordinal)];
    }
}
