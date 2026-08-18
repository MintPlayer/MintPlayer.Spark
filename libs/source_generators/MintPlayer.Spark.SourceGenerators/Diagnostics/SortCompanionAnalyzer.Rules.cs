using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

public sealed partial class SortCompanionAnalyzer
{
    /// <summary>
    /// Warning, not error, on purpose: this is a correctness <em>risk</em> rather than a broken contract, and
    /// the five hand-written index pairs already in the tree must keep compiling while they are flagged.
    /// </summary>
    internal static readonly DiagnosticDescriptor MissingSortCompanionRule = new(
        id: "SPARK005",
        title: "Indexed field has no sort companion",
        messageFormat: "Field '{0}' on index entity '{1}' is indexed for search or exact matching, so ordering on it is unreliable. Add an [IgnoreProperty] '{2}' property mapped from the same value.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An analyzed field is tokenized, so a value containing spaces is stored as several terms and ordering on it is meaningless. A companion field with no indexing declared keeps the whole value as a single term and is what ordering should use.");

    /// <summary>
    /// The hazard that comes with generating only half of a hand-written pair: a generator can add members to a
    /// partial class but cannot reach inside a hand-written <c>Map = ... select new VCar { ... }</c> initializer,
    /// so a companion can be declared, stored and sortable while being fed by nothing.
    /// </summary>
    internal static readonly DiagnosticDescriptor UnassignedSortCompanionRule = new(
        id: "SPARK006",
        title: "Sort companion is never assigned in the index map",
        messageFormat: "Sort companion '{0}' is never assigned in index '{1}', so it will always be empty. Map it from the same value as its base field.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A declared but unmapped companion indexes as null for every document. Nothing fails: the index deploys, reports healthy, and returns the right number of rows with an empty sort key.");
}
