using Microsoft.CodeAnalysis;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

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
    /// A type that needs a key generated and is not <c>partial</c> is reported, never skipped.
    /// Skipping would leave it keyless and the save-time diff unable to judge its collection — a
    /// silent hole where the whole point is that the hole cannot exist.
    /// </summary>
    /// <remarks>
    /// A type carrying <c>[ValueKey]</c> is exempt: nothing is emitted for it, so <c>partial</c>
    /// buys nothing.
    /// </remarks>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
        => valueObjects
            .Where(static v => !v.IsPartial && v.ExistingKeyProperty is null)
            .Select(v => ValueObjectMustBePartialRule.Create(
                v.Location?.ToLocation(compilation), v.Name));
}
