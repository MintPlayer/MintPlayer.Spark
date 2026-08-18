using Microsoft.CodeAnalysis;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// A diagnostic a generator wants reported, carried through the pipeline without a <c>Location</c>.
/// <para>
/// <c>Location</c> holds a <c>SyntaxTree</c> and so must never sit in a pipeline model — it would pin a
/// compilation alive and defeat value comparison. <see cref="LocationKey"/> is the equatable stand-in;
/// it is turned back into a real <c>Location</c> against the current compilation at report time.
/// </para>
/// <para>
/// Deliberately not <c>[AutoValueComparer]</c>: it holds a <see cref="DiagnosticDescriptor"/>, a Roslyn
/// type that value comparison has no business walking. Descriptors are static singletons, so holding one
/// is safe; comparing one structurally is not.
/// </para>
/// </summary>
internal sealed class DiagnosticInfo
{
    public DiagnosticDescriptor Descriptor { get; set; } = null!;

    public LocationKey? Location { get; set; }

    public object[] MessageArgs { get; set; } = [];
}
