using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK020: <c>[Authorize]</c> — or minimal-API <c>.RequireAuthorization(…)</c> — with a policy or
/// roles, in an app whose authorization is Spark's.
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
/// The endpoint-convention form is the same rule spelled as a call:
/// <c>AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization</c> with any policy name
/// (<c>params string[]</c> — a name whatever expression produces it), or with a
/// <c>new AuthorizeAttribute(…)</c> carrying a policy or roles (<c>params IAuthorizeData[]</c>). Every
/// overload is a generic <c>TBuilder : IEndpointConventionBuilder</c> extension, so one match covers
/// <c>MapGet</c>, <c>MapGroup</c> and anything else that returns a convention builder. The method is
/// resolved through the semantic model and its containing type compared exactly, so a user method of
/// the same name is not judged. Not reported: <c>RequireAuthorization()</c> (the default policy —
/// sign-in only, like a bare <c>[Authorize]</c>), a <c>SparkAuthorizeAttribute</c> argument, a
/// schemes-only <c>AuthorizeAttribute</c>, and the <c>AuthorizationPolicy</c> /
/// <c>Action&lt;AuthorizationPolicyBuilder&gt;</c> overloads, which hand over a policy object rather than
/// look one up by name. (A <c>RequireRole</c> inside such a policy has the same blindness to
/// <c>group</c> claims; it is not reported because a policy built in code is an explicit decision,
/// not a string that silently resolves to nothing.)
/// </para>
/// <para>
/// Gated on <c>MintPlayer.Spark.Services.SparkAuthorizeAttribute</c> being resolvable. The analyzer
/// ships with Spark, so in practice that always holds; the gate keeps the rule honest where it does
/// not (a compilation that cannot see Spark is not one whose policies Spark owns), and it guarantees
/// the replacement the message recommends actually compiles there.
/// </para>
/// <para>
/// A syntax-node action on each attribute rather than a symbol action, so an attribute on a lambda
/// (<c>app.MapGet("/", [Authorize(Roles = "x")] () => …)</c>) is covered alongside classes and methods;
/// an operation action on each invocation for the call form.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AuthorizeAttributeAnalyzer : DiagnosticAnalyzer
{
    private const string AuthorizeAttributeMetadataName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string AuthorizeDataMetadataName = "Microsoft.AspNetCore.Authorization.IAuthorizeData";
    private const string SparkAuthorizeAttributeMetadataName = "MintPlayer.Spark.Services.SparkAuthorizeAttribute";
    private const string ConventionExtensionsMetadataName = "Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions";

    private const string AttributeForm = "[Authorize]";
    private const string AttributeReplacement = "[SparkAuthorize(\"Action\", nameof(Target))]";
    private const string PolicyNameForm = "RequireAuthorization";
    private const string AuthorizeDataForm = "RequireAuthorization(new AuthorizeAttribute(…))";
    private const string CallReplacement = ".RequireAuthorization(new SparkAuthorizeAttribute(\"Action\", nameof(Target)))";

    /// <summary>
    /// One descriptor for both spellings: the rule, the reason and the fix are the same, only the
    /// written form ({0}), what it carries ({1}) and the replacement spelled in that form ({2}) differ.
    /// </summary>
    internal static readonly DiagnosticDescriptor PolicyOrRolesOnAuthorizeRule = new(
        id: "SPARK020",
        title: "[Authorize] or RequireAuthorization with a policy or roles does not work under Spark",
        messageFormat: "{0} with {1} does not work under Spark — use {2} to check the security.json right instead; Spark registers no named policies (a Policy throws at request time) and Roles does not see Spark's 'group' claims",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "UseSpark() registers no named authorization policies, so [Authorize(\"X\")], [Authorize(Policy = \"X\")] and .RequireAuthorization(\"X\") throw 'policy not found' on the first request. Roles is checked against role claims only, while Spark carries a user's groups as 'group' claims, so a Roles check fails for them. [SparkAuthorize(\"Action\", nameof(Target))] — or .RequireAuthorization(new SparkAuthorizeAttribute(\"Action\", nameof(Target))) on an endpoint — checks the same security.json right the Spark endpoints check. A bare [Authorize] or .RequireAuthorization() (sign-in only), a schemes-only [Authorize(AuthenticationSchemes = ...)], and the AuthorizationPolicy / Action<AuthorizationPolicyBuilder> overloads are not reported.");

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

            var conventions = start.Compilation.GetTypeByMetadataName(ConventionExtensionsMetadataName);
            var authorizeData = start.Compilation.GetTypeByMetadataName(AuthorizeDataMetadataName);
            if (conventions is not null && authorizeData is not null)
                start.RegisterOperationAction(
                    c => AnalyzeInvocation(c, authorize, authorizeData, conventions), OperationKind.Invocation);
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
            AttributeForm,
            string.Join(" and ", offending),
            AttributeReplacement));
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol authorize,
        INamedTypeSymbol authorizeData,
        INamedTypeSymbol conventions)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "RequireAuthorization") return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType?.OriginalDefinition, conventions)) return;

        foreach (var argument in invocation.Arguments)
        {
            // Skip the `this TBuilder builder` receiver; only the params array carries names or attributes.
            if (argument.Parameter is not { IsParams: true, Type: IArrayTypeSymbol { ElementType: var element } }) continue;

            if (element.SpecialType == SpecialType.System_String)
            {
                if (IsProvablyEmpty(argument.Value)) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    PolicyOrRolesOnAuthorizeRule,
                    NameLocation(invocation),
                    PolicyNameForm,
                    "a policy name",
                    CallReplacement));
            }
            else if (SymbolEqualityComparer.Default.Equals(element, authorizeData)
                     && argument.Value is IArrayCreationOperation { Initializer: { } initializer })
            {
                foreach (var item in initializer.ElementValues)
                {
                    var offending = Offending(Unwrap(item), authorize);
                    if (offending is null) continue;

                    context.ReportDiagnostic(Diagnostic.Create(
                        PolicyOrRolesOnAuthorizeRule,
                        item.Syntax.GetLocation(),
                        AuthorizeDataForm,
                        offending,
                        CallReplacement));
                }
            }
        }
    }

    /// <summary>
    /// "Policy", "Roles", "Policy and Roles" for a <c>new AuthorizeAttribute(…)</c> of the exact type that
    /// carries them, otherwise <see langword="null"/>.
    /// </summary>
    private static string? Offending(IOperation value, INamedTypeSymbol authorize)
    {
        if (value is not IObjectCreationOperation creation) return null;
        if (!SymbolEqualityComparer.Default.Equals(creation.Type, authorize)) return null;

        var offending = new List<string>();
        if (creation.Arguments.Length > 0) offending.Add("Policy");

        if (creation.Initializer is { } initializer)
        {
            foreach (var init in initializer.Initializers)
            {
                if (init is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: var name } }
                    && name is "Policy" or "Roles"
                    && !offending.Contains(name))
                {
                    if (name == "Policy") offending.Insert(0, name);
                    else offending.Add(name);
                }
            }
        }

        return offending.Count == 0 ? null : string.Join(" and ", offending);
    }

    /// <summary>An array whose length is a compile-time zero (<c>new string[0]</c>, <c>new string[] { }</c>).</summary>
    private static bool IsProvablyEmpty(IOperation value)
        => value is IArrayCreationOperation creation
           && (creation.Initializer is { ElementValues.Length: 0 }
               || (creation.Initializer is null
                   && creation.DimensionSizes.Length == 1
                   && creation.DimensionSizes[0].ConstantValue is { HasValue: true, Value: 0 }));

    private static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value;
    }

    /// <summary>The <c>RequireAuthorization</c> name, not the whole fluent chain it ends.</summary>
    private static Location NameLocation(IInvocationOperation invocation)
        => invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
            ? member.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
