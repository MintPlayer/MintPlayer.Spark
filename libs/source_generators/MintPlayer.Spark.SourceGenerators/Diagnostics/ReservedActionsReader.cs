using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>A reserved verb as the compilation sees it: the constant's value, and who declared it.</summary>
internal sealed class ReservedAction(string verb, string declaringType, string assembly, bool isCombined)
{
    public string Verb { get; } = verb;
    public string DeclaringType { get; } = declaringType;
    public string Assembly { get; } = assembly;
    public bool IsCombined { get; } = isCombined;
}

/// <summary>
/// Reads every <c>[assembly: SparkReservedActions(typeof(X))]</c> in the compilation and its referenced
/// assemblies (#460 contributions, Q2): the attribute's type argument, then that type's
/// <c>public const string</c> fields through <see cref="IFieldSymbol.ConstantValue"/> — a constant
/// survives into metadata, so a referenced package's verbs are readable without loading it.
/// </summary>
/// <remarks>
/// The one source of reserved verbs for SPARK023 and for SPARK011's list of actions Spark asks for,
/// mirrored at run time by <c>SparkReservedActionRegistry</c>.
/// </remarks>
internal static class ReservedActionsReader
{
    internal const string AttributeMetadataName = "MintPlayer.Spark.Abstractions.Authorization.SparkReservedActionsAttribute";
    private const string NotAnActionMetadataName = "MintPlayer.Spark.Abstractions.Authorization.SparkNotAnActionAttribute";

    /// <summary>The combined verbs' class: its verbs stand for several others (<c>QueryRead</c>).</summary>
    private const string CombinedActionsMetadataName = "MintPlayer.Spark.Abstractions.Authorization.SparkCombinedActions";

    public static ImmutableArray<ReservedAction> Read(Compilation compilation)
    {
        var result = ImmutableArray.CreateBuilder<ReservedAction>();
        var seen = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);

        foreach (var assembly in new[] { compilation.Assembly }.Concat(compilation.SourceModule.ReferencedAssemblySymbols))
        {
            if (!seen.Add(assembly))
                continue;

            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != AttributeMetadataName)
                    continue;
                if (attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol verbs)
                    continue;

                var declaringType = verbs.ToDisplayString();
                var isCombined = declaringType == CombinedActionsMetadataName;
                foreach (var field in verbs.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!field.IsConst || field.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    if (field.ConstantValue is not string verb || verb.Length == 0)
                        continue;
                    if (field.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == NotAnActionMetadataName))
                        continue;

                    result.Add(new ReservedAction(verb, declaringType, assembly.Name, isCombined));
                }
            }
        }

        return result.ToImmutable();
    }
}
