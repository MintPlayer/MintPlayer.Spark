using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Emits a <c>partial</c> half with the undeclared contract members of every type
/// <see cref="AuditMembersGenerator"/> found. A type that is not partial is skipped here and reported as
/// SPARK038, never dropped quietly.
/// </summary>
public class AuditMembersProducer : Producer
{
    private readonly ImmutableArray<AuditTypeInfo> types;
    private readonly bool canReference;

    public AuditMembersProducer(ImmutableArray<AuditTypeInfo> types, bool canReference, string rootNamespace)
        : base(rootNamespace, "SparkAuditMembers.g.cs")
    {
        this.types = types;
        this.canReference = canReference;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var emitted = types.Where(static t => t.IsPartial).ToArray();
        if (emitted.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        foreach (var group in emitted.GroupBy(static t => t.PathSpec?.ContainingNamespace ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? namespaceBlock = string.IsNullOrEmpty(group.Key)
                ? null
                : writer.OpenBlock($"namespace {group.Key}");

            foreach (var type in group)
            {
                using var parents = writer.OpenPathSpec(type.PathSpec);
                using (writer.OpenBlock(type.Header))
                {
                    var first = true;
                    foreach (var name in type.MissingMembers.Split(','))
                    {
                        if (AuditContracts.FindMember(name) is not { } member)
                            continue;

                        if (!first)
                            writer.WriteLine();
                        first = false;
                        WriteMember(writer, member);
                    }
                }
            }

            namespaceBlock?.Dispose();
        }
    }

    private void WriteMember(IndentedTextWriter writer, AuditContracts.Member member)
    {
        writer.WriteLine($"/// <summary>{member.Summary}</summary>");
        // The framework stamps it, so the model synchronizer creates it read-only (and the server then
        // refuses a posted value) instead of as an editable field.
        writer.WriteLine("[global::System.ComponentModel.ReadOnly(true)]");
        if (member.IsUserId && canReference)
            writer.WriteLine($"[global::{AuditContracts.ReferenceAttribute}(typeof(global::{AuditContracts.SparkUser}))]");
        writer.WriteLine($"public {member.Type} {member.Name} {{ get; set; }}");
    }
}
