using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Which classes <c>AddEntityRedirects</c> registers (#602). Linked into the AllFeatures generator, which calls
/// <c>AddEntityRedirects</c> exactly when this finds one; a different filter there would call a method never generated.
/// </summary>
public static class EntityRedirectFilter
{
    public const string EntityRedirect = "MintPlayer.Spark.Abstractions.Links.ISparkEntityRedirect";
    public const string RegistrationExtensions = "MintPlayer.Spark.Extensions.SparkBuilderPrerenderingExtensions";

    /// <summary>A concrete, closed, nameable implementation of <c>ISparkEntityRedirect</c>.</summary>
    public static bool IsRegistrable(INamedTypeSymbol? classSymbol)
        => classSymbol is { IsAbstract: false, IsGenericType: false }
            && classSymbol.DeclaredAccessibility != Accessibility.Private
            && classSymbol.AllInterfaces.Any(i => i.ToDisplayString() == EntityRedirect);
}
