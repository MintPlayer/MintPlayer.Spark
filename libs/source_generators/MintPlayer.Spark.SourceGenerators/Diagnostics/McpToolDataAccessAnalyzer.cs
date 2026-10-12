using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK050: unchecked data access inside an MCP tool class (<c>[McpServerToolType]</c>).
/// </summary>
/// <remarks>
/// <para>
/// An MCP tool runs as the user who connected the agent (MCP PRD §4.5). Spark enforces that user's rights,
/// row security, attribute redaction and the agent's permissions in its checked services —
/// <c>IDatabaseAccess</c>, <c>IQueryExecutor</c>, <c>IPermissionService</c>. A raw RavenDB session or store
/// enforces none of it, and neither do <c>IDatabaseAccess</c>'s <c>…UncheckedAsync</c> methods, which are a thin
/// typed wrapper over the session: a tool built on them hands the agent every document in the collection.
/// </para>
/// <para>
/// Reported: a field, property or constructor/method parameter of the tool class typed as
/// <c>IAsyncDocumentSession</c>, <c>IDocumentSession</c> or <c>IDocumentStore</c> (or a type implementing
/// one), and every call into one of those, or into an <c>…UncheckedAsync</c> method of <c>IDatabaseAccess</c>,
/// made from inside the class. A warning, not an error: a tool can have a legitimate reason (a lookup of
/// public reference data), and suppressing it at the call site is the record of that decision.
/// </para>
/// <para>
/// Gated on <c>ModelContextProtocol.Server.McpServerToolTypeAttribute</c> being resolvable, so a compilation
/// without the MCP SDK pays nothing.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class McpToolDataAccessAnalyzer : DiagnosticAnalyzer
{
    private const string ToolTypeAttributeMetadataName = "ModelContextProtocol.Server.McpServerToolTypeAttribute";
    private const string DatabaseAccessMetadataName = "MintPlayer.Spark.Abstractions.IDatabaseAccess";

    private static readonly string[] RawMetadataNames =
    [
        "Raven.Client.Documents.Session.IAsyncDocumentSession",
        "Raven.Client.Documents.Session.IDocumentSession",
        "Raven.Client.Documents.IDocumentStore",
    ];

    internal static readonly DiagnosticDescriptor UncheckedDataAccessRule = new(
        id: "SPARK050",
        title: "MCP tool uses unchecked data access",
        messageFormat: "{0} in MCP tool class '{1}' enforces no rights, row security or agent permissions — use IDatabaseAccess (not its …UncheckedAsync methods), IQueryExecutor or IPermissionService",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An MCP tool runs as the user who connected the agent. A raw RavenDB session or store, and IDatabaseAccess's …UncheckedAsync methods, bypass the user's security.json rights, row security, attribute redaction and the agent's permissions, so the agent can read every document in the collection. Use the checked services instead.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UncheckedDataAccessRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var toolType = start.Compilation.GetTypeByMetadataName(ToolTypeAttributeMetadataName);
            if (toolType is null) return;

            var raw = RawMetadataNames
                .Select(start.Compilation.GetTypeByMetadataName)
                .Where(t => t is not null)
                .Cast<INamedTypeSymbol>()
                .ToImmutableArray();
            var databaseAccess = start.Compilation.GetTypeByMetadataName(DatabaseAccessMetadataName);
            if (raw.IsEmpty && databaseAccess is null) return;

            start.RegisterSymbolAction(c => AnalyzeMembers(c, toolType, raw), SymbolKind.NamedType);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, toolType, raw, databaseAccess), OperationKind.Invocation);
        });
    }

    private static void AnalyzeMembers(SymbolAnalysisContext context, INamedTypeSymbol toolType, ImmutableArray<INamedTypeSymbol> raw)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!IsToolClass(type, toolType)) return;

        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;

            switch (member)
            {
                case IFieldSymbol field when IsRaw(field.Type, raw):
                    Report(context, member, $"'{field.Type.Name}' field '{field.Name}'", type);
                    break;
                case IPropertySymbol property when IsRaw(property.Type, raw):
                    Report(context, member, $"'{property.Type.Name}' property '{property.Name}'", type);
                    break;
                case IMethodSymbol method:
                    foreach (var parameter in method.Parameters.Where(p => IsRaw(p.Type, raw)))
                        Report(context, parameter, $"'{parameter.Type.Name}' parameter '{parameter.Name}'", type);
                    break;
            }
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol toolType, ImmutableArray<INamedTypeSymbol> raw, INamedTypeSymbol? databaseAccess)
    {
        var owner = context.ContainingSymbol.ContainingType;
        if (owner is null || !IsToolClass(owner, toolType)) return;

        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var declaring = method.ContainingType;
        if (declaring is null) return;

        // The receiver, not only the declaring type: a session method may be declared on a base interface
        // (Raven's session interfaces are layered) that is not itself one of the raw types.
        var receiver = invocation.Instance?.Type;

        string? what = null;
        if (receiver is not null && IsRaw(receiver, raw))
            what = $"'{receiver.Name}.{method.Name}'";
        else if (IsRaw(declaring, raw))
            what = $"'{declaring.Name}.{method.Name}'";
        else if (databaseAccess is not null && method.Name.EndsWith("UncheckedAsync") && Implements(declaring, databaseAccess))
            what = $"'IDatabaseAccess.{method.Name}'";

        if (what is null) return;

        context.ReportDiagnostic(Diagnostic.Create(UncheckedDataAccessRule, invocation.Syntax.GetLocation(), what, owner.Name));
    }

    /// <summary>The class, or one it is nested in, carries <c>[McpServerToolType]</c>.</summary>
    private static bool IsToolClass(INamedTypeSymbol type, INamedTypeSymbol toolType)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, toolType)))
                return true;
        }
        return false;
    }

    private static bool IsRaw(ITypeSymbol type, ImmutableArray<INamedTypeSymbol> raw)
        => raw.Any(r => Implements(type, r));

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol target)
        => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target)
           || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target));

    private static void Report(SymbolAnalysisContext context, ISymbol symbol, string what, INamedTypeSymbol owner)
    {
        // A member a generator wrote (an [Inject] constructor) is reported at the field it was generated from.
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && !IsGenerated(l));
        if (location is null) return;
        context.ReportDiagnostic(Diagnostic.Create(UncheckedDataAccessRule, location, what, owner.Name));
    }

    private static bool IsGenerated(Location location)
    {
        var path = location.SourceTree?.FilePath ?? string.Empty;
        return path.EndsWith(".g.cs") || path.EndsWith(".generated.cs");
    }
}
