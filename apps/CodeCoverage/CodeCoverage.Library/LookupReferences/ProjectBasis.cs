using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// Which number a <em>partial</em> build's project status is judged on. <c>[SparkLookup]</c> generates the
/// <c>ProjectBasis</c> lookup with a <c>const string</c> per member (<c>ProjectBasis.Scoped</c> is
/// <c>"scoped"</c>); its labels are the <c>translations.json</c> keys <c>lookup.EProjectBasis.*</c>.
/// </summary>
/// <remarks>
/// String-keyed for the same reasons as <see cref="EProjectComparison"/> — the keys are a public
/// <c>coverage.yml</c> vocabulary and are already stored. This enum only declares the lookup: no entity
/// stores it.
/// <para>
/// ⚠️ <c>GateEvaluator</c> has a third basis, <c>"whole"</c>, which it substitutes for a build that
/// is not partial. That is a derived state, never a choice, so it is deliberately absent here. Do
/// not add it: it would offer the user a setting that the evaluator overwrites.
/// </para>
/// </remarks>
[SparkLookup(StringKeys = true)]
public enum EProjectBasis
{
    /// <summary>Like-for-like: the baseline is scoped to what this build measured. Compare only what this build measured.</summary>
    [SparkLookupKey("scoped")]
    Scoped,

    /// <summary>The patched projection across the whole workspace. Project the patch across the whole workspace.</summary>
    [SparkLookupKey("projection")]
    Projection,
}
