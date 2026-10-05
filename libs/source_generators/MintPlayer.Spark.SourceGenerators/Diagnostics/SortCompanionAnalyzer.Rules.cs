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
    /// <remarks>
    /// Covers every companion kind since 2026-10-05 — <c>{Name}Search</c>, <c>{Name}Sort</c> and the
    /// <c>{Name}Raw</c> offset wrapper — and reports on the base field rather than the companion, so a
    /// generated companion is reachable (docs/datetimeoffset_query_sort_filter_PRD.md, D15). Still a
    /// warning: silent wrong data, by the rule at the top of this file, and an existing id must not start
    /// breaking builds.
    /// </remarks>
    internal static readonly DiagnosticDescriptor UnassignedSortCompanionRule = new(
        id: "SPARK006",
        title: "Index companion is never assigned in the index map",
        messageFormat: "Index companion '{0}' is never assigned in index '{1}', so {2}. Map it from the same value as '{3}'.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A declared but unmapped companion indexes as null for every document. Nothing fails: the index deploys, reports healthy and returns the right rows in the right order, while search on a {Name}Search companion matches nothing and a {Name}Raw companion returns every DateTimeOffset at +00:00.");

    /// <summary>
    /// The hole that had no guard at all: the generator emits the <c>Index(...)</c> calls into a
    /// <c>private void IndexSearchFields()</c>, and nothing verified the constructor calls it. An
    /// uncalled private method raises no compiler warning, so the documentation in
    /// <c>docs/guide-queries-and-sorting.md</c> ("You must call it", in bold) was the only check.
    /// </summary>
    /// <remarks>
    /// ⚠️ The <c>DateTimeOffset</c> case is the severe one and is easy to miss, because it has nothing
    /// to do with search: without its wrapper companion declared <c>FieldIndexing.No</c>, Corax parks
    /// the whole index at <c>state=Error, entries=0</c> after a clean deploy. The <c>[Search]</c> case
    /// merely loses full-text search silently.
    /// <para>
    /// Deriving from <c>SparkIndexCreationTask&lt;T&gt;</c> closes this structurally, so such an index
    /// is <b>exempt</b> rather than flagged — there is no constructor call to look for.
    /// </para>
    /// </remarks>
    internal static readonly DiagnosticDescriptor UncalledIndexSearchFieldsRule = new(
        id: "SPARK018",
        title: "Generated index field configuration is never applied",
        messageFormat: "Index '{0}' never calls '{1}()', so the field options generated from '{2}' are not applied. Derive from SparkIndexCreationTask<T> so it is called automatically, or call it from every constructor.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator can add members to a partial class but cannot add statements to a hand-written constructor, so the Index(...) calls it produces have to be invoked explicitly. Nothing else detects the omission: the index deploys, reports healthy and returns correct row counts, while full-text search silently matches nothing — or, for a DateTimeOffset field, the index fails to build at all.");
}
