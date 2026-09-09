using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Emits a <c>partial</c> half carrying the row key for every <c>[ValueObject]</c> that needs one,
/// plus the registry that tells the runtime which types have one and how to read it.
/// </summary>
public class ValueObjectKeyProducer : Producer
{
    private readonly ImmutableArray<ValueObjectInfo> valueObjects;

    public ValueObjectKeyProducer(ImmutableArray<ValueObjectInfo> valueObjects, string rootNamespace)
        : base(rootNamespace, "SparkValueObjectKeys.g.cs")
    {
        this.valueObjects = valueObjects;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        // Registered: everything that has a key, however it got one. A [ValueKey] type is registered
        // and nothing is emitted for it — being absent from the registry is not neutral, it makes
        // the type's collections unjudgeable at save time.
        var registered = valueObjects
            .Where(static v => v.ExistingKeyProperty is not null || v.IsPartial)
            .ToArray();

        // Emitted for: only those with no key of their own. A non-partial type needing one is
        // dropped here and reported as SPARK016 — never dropped quietly.
        var emitted = registered.Where(static v => v.ExistingKeyProperty is null).ToArray();

        if (registered.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        foreach (var group in emitted.GroupBy(static v => v.PathSpec?.ContainingNamespace ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? namespaceBlock = string.IsNullOrEmpty(group.Key)
                ? null
                : writer.OpenBlock($"namespace {group.Key}");

            foreach (var valueObject in group)
            {
                // Containing types come from the symbol, never the file path: namespace does not
                // follow folder in most apps here, and several targets share a source file.
                using var parents = writer.OpenPathSpec(valueObject.PathSpec);
                using (writer.OpenBlock($"partial class {valueObject.Name}"))
                {
                    writer.WriteLine("/// <summary>");
                    writer.WriteLine("/// Identifies this row across saves, so the framework can tell an added row from a");
                    writer.WriteLine("/// removed one instead of replacing the collection wholesale.");
                    writer.WriteLine("/// </summary>");
                    writer.WriteLine("/// <remarks>");
                    writer.WriteLine("/// Minted by the field initializer, so it exists however the object is constructed —");
                    writer.WriteLine("/// including the framework's own <c>Activator.CreateInstance</c> on every save. An");
                    writer.WriteLine("/// existing row's stored key overwrites it on the way in; a genuinely new row keeps");
                    writer.WriteLine("/// the fresh one.");
                    writer.WriteLine("/// </remarks>");
                    // Marked, so the type reads the same whether its key was generated or written by
                    // hand -- and so anything reasoning about keys has one thing to look for.
                    writer.WriteLine("[global::MintPlayer.Spark.Abstractions.ValueKey]");
                    writer.WriteLine("public string Id { get; set; } = global::System.Guid.NewGuid().ToString(\"N\");");
                }
            }

            namespaceBlock?.Dispose();
        }

        writer.WriteLine();
        WriteRegistry(writer, registered, cancellationToken);
    }

    /// <summary>
    /// Registers this assembly's value objects with the runtime.
    /// </summary>
    /// <remarks>
    /// Per assembly, deliberately. The runtime sees the union of every loaded assembly's
    /// registration, which is what lets an element type declared in one assembly and used from
    /// another be keyed without either side knowing about the other.
    /// <para>
    /// The accessor is emitted as a typed lambda rather than a property name, so reading a key costs
    /// a delegate call — <c>Type.GetProperty</c> is the expensive part of reflection, and this runs
    /// once per row per save.
    /// </para>
    /// </remarks>
    private void WriteRegistry(
        IndentedTextWriter writer, ValueObjectInfo[] targets, CancellationToken cancellationToken)
    {
        using (writer.OpenBlock($"namespace {RootNamespace}"))
        using (writer.OpenBlock("internal static class SparkValueObjectRegistration"))
        {
            writer.WriteLine("[global::System.Runtime.CompilerServices.ModuleInitializer]");
            using (writer.OpenBlock("internal static void Register()"))
            {
                foreach (var valueObject in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Either the property the author marked [ValueKey], or the Id emitted above.
                    var key = valueObject.ExistingKeyProperty ?? "Id";
                    writer.WriteLine(
                        "global::MintPlayer.Spark.Abstractions.Model.SparkValueObjects.Register(" +
                        $"typeof({valueObject.FullyQualifiedName}), static o => (({valueObject.FullyQualifiedName})o).{key}?.ToString());");
                }
            }
        }
    }
}
