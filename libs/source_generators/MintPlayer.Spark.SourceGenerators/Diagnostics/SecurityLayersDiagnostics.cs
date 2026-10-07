using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Library rights (composition D4), reported where they are written: in the library's own build
/// (<see cref="Generators.LibraryLayersGenerator"/>, the guard rails) and in the application's
/// (<see cref="SecurityConfigurationAnalyzer"/>, the composed set). Errors, because the host refuses
/// the same composition at startup with the same sentences (<see cref="SparkSecurityLayers"/>).
/// </summary>
internal static class SecurityLayersDiagnostics
{
    public static readonly DiagnosticDescriptor GuardRail = new(
        id: "SPARK047",
        title: "A library's security.json breaks a guard rail",
        messageFormat: "Library '{0}' {1}",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A library may only grant (no isDenied, no isImportant), only on the types, queries and reservedTargets it ships, and only to @anonymous, @authenticated or its own slots ('<alias>:<slot>'), which the application binds. Everything else in security.json is the application's.");

    public static readonly DiagnosticDescriptor Unresolved = new(
        id: "SPARK048",
        title: "A security group token or slot does not resolve",
        messageFormat: "{0}: {1}",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "@anonymous and @authenticated resolve through wellKnown, a slot through bindings. A right whose group resolves to nothing would grant to nobody, so the host refuses it.");

    public static readonly DiagnosticDescriptor Composition = new(
        id: "SPARK049",
        title: "security.json states something about library rights that cannot mean what it says",
        messageFormat: "{0}: {1}",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The application removes a library grant by key ({\"key\": \"<alias>:<key>\", \"$remove\": true}), never edits one, and opts out of or binds only libraries it references.");

    public static DiagnosticDescriptor For(SparkSecurityProblemKind kind) => kind switch
    {
        SparkSecurityProblemKind.GuardRail => GuardRail,
        SparkSecurityProblemKind.Unresolved => Unresolved,
        _ => Composition,
    };
}
