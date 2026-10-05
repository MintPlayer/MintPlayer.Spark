using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// The header a generator writes to reopen a type as <c>partial</c>: the right keyword for a record or a
/// struct, and the type parameters of a generic type. <c>OpenPathSpec</c> reopens the containing types;
/// this is the target type's own line.
/// </summary>
/// <remarks>
/// The <c>[ValueObject]</c> producer used to hard-code <c>partial class {Name}</c>, which a record
/// (CS0261) and a generic type (a second, unrelated type) both broke (#271, F1).
/// </remarks>
internal static class PartialTypeHeader
{
    public static string For(INamedTypeSymbol type)
    {
        var keyword = type.TypeKind switch
        {
            TypeKind.Struct when type.IsRecord => "record struct",
            TypeKind.Struct => "struct",
            _ when type.IsRecord => "record",
            _ => "class",
        };

        var typeParameters = type.TypeParameters.IsEmpty
            ? string.Empty
            : "<" + string.Join(", ", type.TypeParameters.Select(static p => p.Name)) + ">";

        return $"partial {keyword} {type.Name}{typeParameters}";
    }

    /// <summary>Whether the type and every type containing it carry <c>partial</c> on every declaration.</summary>
    public static bool IsPartialAllTheWayUp(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            var declarations = t.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(cancellationToken))
                .OfType<TypeDeclarationSyntax>()
                .ToList();
            if (declarations.Count == 0 || !declarations.All(static d => d.Modifiers.Any(SyntaxKind.PartialKeyword)))
                return false;
        }
        return true;
    }
}
