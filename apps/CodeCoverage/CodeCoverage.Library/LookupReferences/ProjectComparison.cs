using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// How a build's project coverage is judged: against the resolved base commit, or against a fixed
/// target.
/// </summary>
/// <remarks>
/// ⚠️ <b>String-keyed, deliberately — do not "tidy" this into an enum.</b> Every other lookup in
/// this repository keys on an enum, and that is the right default. It is wrong here because the keys
/// are not ours to choose. <c>auto</c> and <c>fixed</c> are a public vocabulary: they are what users
/// write in a <c>coverage.yml</c> in their own repositories (documented in
/// <c>docs/code-coverage/upload-api.md</c>), and they are what is already stored in
/// <c>Repositories.Gate</c> and in the <c>GateSnapshot</c> of every published build.
/// <para>
/// RavenDB persists enums by NAME, so an <c>EProjectMode.Auto</c> would store <c>"Auto"</c> — a
/// rewrite of both collections and a divergence from the YAML vocabulary that cannot follow, since
/// those files live in other people's repositories. Keying the lookup on the string leaves every
/// stored byte untouched and keeps one vocabulary instead of two.
/// </para>
/// <para>
/// It also sidesteps a silent failure: <c>EntityMapper</c> converts a posted key with the
/// case-sensitive <c>Enum.Parse</c> overload inside a <c>try</c> whose <c>catch</c> swallows
/// everything, so an enum whose member casing disagreed with the stored value would make saving
/// report success and change nothing. A string target takes the <c>Convert.ChangeType</c> branch,
/// where there is nothing to get wrong.
/// </para>
/// </remarks>
public sealed class ProjectComparison : TransientLookupReference<string>
{
    private ProjectComparison() { }

    /// <summary>Ratchet against the resolved base commit.</summary>
    public const string Auto = "auto";

    /// <summary>Compare to <c>GateSettings.ProjectTarget</c>; no base commit is needed.</summary>
    public const string Fixed = "fixed";

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ProjectComparison> Items { get; } =
    [
        new ProjectComparison
        {
            Key = Auto,
            Description = "Compare against the base commit and fail on a drop",
            Values = _TS(
                "Ratchet against the base commit",
                "Comparer au commit de base",
                "Vergelijken met de basiscommit"),
        },
        new ProjectComparison
        {
            Key = Fixed,
            Description = "Compare against a fixed percentage target",
            Values = _TS("Fixed target", "Cible fixe", "Vaste drempel"),
        },
    ];
}
