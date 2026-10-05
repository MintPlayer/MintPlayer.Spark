using Microsoft.CodeAnalysis;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Reports what <see cref="AuditMembersProducer"/> could not, or did not, emit as asked:
/// SPARK038 for a type that is not <c>partial</c>, SPARK040 for user-id members generated without
/// <c>[Reference]</c>.
/// </summary>
public partial class AuditMembersReporter : IDiagnosticReporter
{
    private readonly ImmutableArray<AuditTypeInfo> types;
    private readonly bool canReference;

    public AuditMembersReporter(ImmutableArray<AuditTypeInfo> types, bool canReference)
    {
        this.types = types;
        this.canReference = canReference;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        foreach (var type in types)
        {
            var location = type.Location?.ToLocation(compilation);
            if (!type.IsPartial)
            {
                yield return AuditTypeMustBePartialRule.Create(location, type.Name, type.MissingMembers.Replace(",", ", "));
                continue;
            }

            if (canReference)
                continue;

            var userIds = type.MissingMembers.Split(',')
                .Where(static name => AuditContracts.FindMember(name)?.IsUserId == true)
                .ToArray();
            if (userIds.Length > 0)
                yield return UserIdWithoutReferenceRule.Create(location, type.Name, string.Join(", ", userIds));
        }
    }
}
