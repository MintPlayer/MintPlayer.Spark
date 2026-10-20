using Microsoft.CodeAnalysis;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Reports the <c>[Replicated]</c> classes <see cref="ReplicatedMembersProducer"/> skipped:
/// SPARK065 (not partial, or generic), SPARK066 (invalid declaration), SPARK067 (path missing on
/// <c>OriginalType</c>), SPARK068 (a hand-written member named like a generated one).
/// </summary>
public partial class ReplicatedReporter : IDiagnosticReporter
{
    /// <summary>Joins a problem's message arguments into one comparable string.</summary>
    internal const string Separator = "\u001F";

    private readonly ImmutableArray<ReplicaInfo> replicas;

    public ReplicatedReporter(ImmutableArray<ReplicaInfo> replicas)
    {
        this.replicas = replicas;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        foreach (var replica in replicas)
        {
            foreach (var problem in replica.Problems)
            {
                var rule = Rule(problem.Id);
                if (rule is null)
                    continue;

                var arguments = new object[] { replica.Name }
                    .Concat(problem.Arguments.Split(new[] { Separator }, StringSplitOptions.None))
                    .ToArray();
                yield return rule.Create(problem.Location?.ToLocation(compilation), arguments);
            }
        }
    }

    private static DiagnosticDescriptor? Rule(string id) => id switch
    {
        MustBePartialId => MustBePartialRule,
        InvalidDeclarationId => InvalidDeclarationRule,
        MissingPathId => MissingPathRule,
        CollidingMemberId => CollidingMemberRule,
        _ => null,
    };
}
