using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Reports the value objects <see cref="ValueObjectKeyProducer"/> could not key.
/// </summary>
/// <remarks>
/// Separate from the producer on purpose: emitting and complaining are answers to different
/// questions, and a type that cannot be keyed is exactly the case where there is nothing to emit —
/// so folding the two together makes the silent-skip path the easy one to write.
/// </remarks>
public partial class ValueObjectKeyReporter : IDiagnosticReporter
{
    private readonly ImmutableArray<ValueObjectInfo> valueObjects;

    public ValueObjectKeyReporter(ImmutableArray<ValueObjectInfo> valueObjects)
    {
        this.valueObjects = valueObjects;
    }

    /// <summary>
    /// A target that is not <c>partial</c> is reported, never skipped. Skipping would leave the type
    /// keyless and the save-time diff unable to judge its collection — a silent hole where the whole
    /// point is that the hole cannot exist.
    /// </summary>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
        => valueObjects
            .Where(static v => !v.IsPartial && !v.DeclaresOwnId)
            .Select(v => ValueObjectMustBePartialRule.Create(
                v.Location?.ToLocation(compilation), v.Name));
}
