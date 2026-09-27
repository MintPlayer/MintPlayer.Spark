using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK020: <c>[Authorize]</c> with a policy or roles, in an app whose authorization is Spark's.
/// </summary>
/// <remarks>
/// Under Spark neither form does what it reads as. <c>UseSpark()</c> registers no named policies, so
/// <c>[Authorize("X")]</c> / <c>[Authorize(Policy = "X")]</c> throws "policy not found" on the first
/// request — at runtime, on the one endpoint, long after the build passed. <c>Roles = …</c> is checked
/// through <c>RequireRole</c>, which sees only role claims; Spark carries a user's groups as
/// <c>group</c> claims, so the check fails for every such user and works only if the group happens to
/// also be stored as an Identity role. <c>[SparkAuthorize("Action", nameof(Target))]</c> checks the
/// same security.json right the persistent-object endpoints check, and is the replacement.
/// <para>
/// Only the exact <c>Microsoft.AspNetCore.Authorization.AuthorizeAttribute</c> is inspected.
/// <c>SparkAuthorizeAttribute</c> derives from it and sets <c>Policy</c> in its constructor, and a
/// user-defined subclass is its author's own decision. A bare <c>[Authorize]</c> (sign-in only) and a
/// schemes-only <c>[Authorize(AuthenticationSchemes = …)]</c> are fine and not reported.
/// </para>
/// <para>
/// Gated on <c>MintPlayer.Spark.Services.SparkAuthorizeAttribute</c> being resolvable. The analyzer
/// ships with Spark, so in practice that always holds; the gate keeps the rule honest where it does
/// not (a compilation that cannot see Spark is not one whose policies Spark owns), and it guarantees
/// the replacement the message recommends actually compiles there.
/// </para>
/// <para>
/// A syntax-node action on each attribute rather than a symbol action, so an attribute on a lambda
/// (<c>app.MapGet("/", [Authorize(Roles = "x")] () => …)</c>) is covered alongside classes and methods.
/// Minimal-API <c>.RequireAuthorization("policy")</c> is a method call, not an attribute, and is not
/// covered.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AuthorizeAttributeAnalyzer : DiagnosticAnalyzer
{
    private const string AuthorizeAttributeMetadataName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string SparkAuthorizeAttributeMetadataName = "MintPlayer.Spark.Services.SparkAuthorizeAttribute";

    internal static readonly DiagnosticDescriptor PolicyOrRolesOnAuthorizeRule = new(
        id: "SPARK020",
        title: "[Authorize] with a policy or roles does not work under Spark",
        messageFormat: "[Authorize] with {0} does not work under Spark — use [SparkAuthorize(\"Action\", nameof(Target))] to check the security.json right instead; Spark registers no named policies (a Policy throws at request time) and Roles does not see Spark's 'group' claims",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "UseSpark() registers no named authorization policies, so [Authorize(\"X\")] and [Authorize(Policy = \"X\")] throw 'policy not found' on the first request. Roles is checked against role claims only, while Spark carries a user's groups as 'group' claims, so a Roles check fails for them. [SparkAuthorize(\"Action\", nameof(Target))] checks the same security.json right the Spark endpoints check. A bare [Authorize] (sign-in only) and a schemes-only [Authorize(AuthenticationSchemes = ...)] are not reported.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [PolicyOrRolesOnAuthorizeRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var authorize = start.Compilation.GetTypeByMetadataName(AuthorizeAttributeMetadataName);
            if (authorize is null) return;
            if (start.Compilation.GetTypeByMetadataName(SparkAuthorizeAttributeMetadataName) is null) return;

            start.RegisterSyntaxNodeAction(c => Analyze(c, authorize), SyntaxKind.Attribute);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol authorize)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (attribute.ArgumentList is not { Arguments.Count: > 0 } arguments) return;

        // Exact type only: SparkAuthorizeAttribute and any user subclass derive from it and are not ours to judge.
        if (context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol
            is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) return;
        if (!SymbolEqualityComparer.Default.Equals(constructor.ContainingType, authorize)) return;

        var offending = new List<string>();
        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameEquals is { } named)
            {
                var name = named.Name.Identifier.ValueText;
                if (name is "Policy" or "Roles" && !offending.Contains(name))
                    offending.Add(name);
            }
            else if (!offending.Contains("Policy"))
            {
                // Positional (optionally `policy:`-named) argument: the AuthorizeAttribute(string policy) ctor.
                offending.Insert(0, "Policy");
            }
        }

        if (offending.Count == 0) return;

        context.ReportDiagnostic(Diagnostic.Create(
            PolicyOrRolesOnAuthorizeRule,
            attribute.GetLocation(),
            string.Join(" and ", offending)));
    }
}
