using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// Which number a <em>partial</em> build's project status is judged on.
/// </summary>
/// <remarks>
/// String-keyed for the same reasons as <see cref="ProjectComparison"/> — the keys are a public
/// <c>coverage.yml</c> vocabulary and are already stored.
/// <para>
/// ⚠️ <c>GateEvaluator</c> has a third basis, <c>"whole"</c>, which it substitutes for a build that
/// is not partial. That is a derived state, never a choice, so it is deliberately absent here. Do
/// not add it: it would offer the user a setting that the evaluator overwrites.
/// </para>
/// </remarks>
public sealed class ProjectBasis : TransientLookupReference<string>
{
    private ProjectBasis() { }

    /// <summary>Like-for-like: the baseline is scoped to what this build measured.</summary>
    public const string Scoped = "scoped";

    /// <summary>The patched projection across the whole workspace.</summary>
    public const string Projection = "projection";

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ProjectBasis> Items { get; } =
    [
        new ProjectBasis
        {
            Key = Scoped,
            Description = "Compare only what this build measured",
            Values = _TS(
                "Scoped baseline (like-for-like)",
                "Référence restreinte (comparable)",
                "Beperkte basislijn (gelijk om gelijk)"),
        },
        new ProjectBasis
        {
            Key = Projection,
            Description = "Project the patch across the whole workspace",
            Values = _TS(
                "Patched projection (whole workspace)",
                "Projection corrigée (espace de travail complet)",
                "Geprojecteerde dekking (volledige workspace)"),
        },
    ];
}
