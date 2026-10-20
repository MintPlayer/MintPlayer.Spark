using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

// The dependency updater's owner settings (docs/code-coverage/dependency-updates-PRD.md §4.1, as §13.10 keeps them: the
// feature switch, the schedule, the cooldown and the bisect budget; thresholds moved to coverage.yml, R6). Each account
// value has a zero value that IS its default, so an account written before the field existed needs no migration to read
// right; the backfill migration only makes the stored documents queryable (M_202610161200).
//
// Each [SparkLookup] enum generates the lookup of the same name without its E prefix (EDependencyCooldown →
// DependencyCooldown), one item per member in declaration order; the labels are the translations.json keys
// lookup.{Enum}.{Member}. ⚠️ Never reorder members to change a dropdown's order: the zero value is the default.

/// <summary>Whether a <c>Repository</c> takes part in dependency updates, or defers to its account (PRD §4.1, G2).</summary>
[SparkLookup]
public enum EDependencyUpdatesPolicy
{
    /// <summary>Use the account's setting. The default for every repository.</summary>
    Inherit,

    /// <summary>Keep a rolling dependency-update pull request in this repository.</summary>
    Enabled,

    /// <summary>No dependency updates in this repository, whatever the account setting says.</summary>
    Disabled,
}

/// <summary>How often the dependency updater recomputes the rolling pull request (Q8).</summary>
/// <remarks>⚠️ <see cref="Weekly"/> is the zero value on purpose: it is Q8's default.</remarks>
[SparkLookup]
public enum EDependencyScheduleFrequency
{
    /// <summary>Once a week, on the chosen weekday.</summary>
    [SparkLookupOrder(2)] Weekly,

    /// <summary>Every day.</summary>
    [SparkLookupOrder(1)] Daily,

    /// <summary>Once a month, on the first chosen weekday.</summary>
    [SparkLookupOrder(3)] Monthly,
}

/// <summary>The weekday of a weekly slot (Q8); ignored for daily. A monthly slot is the first such weekday of the month.</summary>
/// <remarks>⚠️ <see cref="Monday"/> is the zero value on purpose: it is Q8's default.</remarks>
[SparkLookup]
public enum EDependencyWeekday
{
    Monday,

    Tuesday,

    Wednesday,

    Thursday,

    Friday,

    Saturday,

    Sunday,
}

/// <summary>Whether a <c>Repository</c> uses its account's schedule or its own.</summary>
/// <remarks>A named state rather than nullable fields, for the reason <see cref="EMergeQueueCheckExclusionsPolicy"/> gives.</remarks>
[SparkLookup]
public enum EDependencySchedulePolicy
{
    /// <summary>Use the account's frequency, weekday and hour. The default.</summary>
    Inherit,

    /// <summary>Use this repository's own frequency, weekday and hour.</summary>
    Override,
}

/// <summary>How old a third-party release must be before it is planned (Q16, §7.6). Own packages are exempt.</summary>
/// <remarks>⚠️ <see cref="ThreeDays"/> is the zero value on purpose: it is Q16's default.</remarks>
[SparkLookup]
public enum EDependencyCooldown
{
    /// <summary>Wait three days after a release.</summary>
    [SparkLookupOrder(3)] ThreeDays,

    /// <summary>Plan a release as soon as it is seen.</summary>
    [SparkLookupOrder(1)] None,

    /// <summary>Wait one day after a release.</summary>
    [SparkLookupOrder(2)] OneDay,

    /// <summary>Wait seven days after a release.</summary>
    [SparkLookupOrder(4)] SevenDays,
}

/// <summary>The cooldown on a <c>Repository</c>, which may defer to its account.</summary>
[SparkLookup]
public enum EDependencyCooldownPolicy
{
    /// <summary>Use the account's cooldown.</summary>
    Inherit,

    /// <summary>Plan a release as soon as it is seen.</summary>
    None,

    /// <summary>Wait one day after a release.</summary>
    OneDay,

    /// <summary>Wait three days after a release.</summary>
    ThreeDays,

    /// <summary>Wait seven days after a release.</summary>
    SevenDays,
}

/// <summary>How many CI runs the updater may spend bisecting a red rolling pull request (Q12, §7.4).</summary>
/// <remarks>⚠️ <see cref="Two"/> is the zero value on purpose: it is Q12's default.</remarks>
[SparkLookup]
public enum EDependencyBisectBudget
{
    /// <summary>At most two CI runs.</summary>
    [SparkLookupOrder(3)] Two,

    /// <summary>Never bisect a red pull request.</summary>
    [SparkLookupOrder(1)] Off,

    /// <summary>At most one CI run.</summary>
    [SparkLookupOrder(2)] One,

    /// <summary>At most four CI runs.</summary>
    [SparkLookupOrder(4)] Four,

    /// <summary>At most eight CI runs.</summary>
    [SparkLookupOrder(5)] Eight,
}

/// <summary>The bisect budget on a <c>Repository</c>, which may defer to its account.</summary>
[SparkLookup]
public enum EDependencyBisectBudgetPolicy
{
    /// <summary>Use the account's bisect budget.</summary>
    Inherit,

    /// <summary>Never bisect a red pull request.</summary>
    Off,

    /// <summary>At most one CI run.</summary>
    One,

    /// <summary>At most two CI runs.</summary>
    Two,

    /// <summary>At most four CI runs.</summary>
    Four,

    /// <summary>At most eight CI runs.</summary>
    Eight,
}

/// <summary>
/// The largest update that is planned (and auto-merged) without an approval (R6): <c>never &lt; patch &lt; minor &lt; major</c>.
/// On an account's <c>DependencyRule</c> it is the rule's ceiling.
/// </summary>
/// <remarks>The order of the members IS the order of the levels; code compares them.</remarks>
[SparkLookup]
public enum EDependencyThreshold
{
    /// <summary>Every update needs an approval.</summary>
    Never,

    /// <summary>Patch updates at most.</summary>
    Patch,

    /// <summary>Minor and patch updates.</summary>
    Minor,

    /// <summary>Any update, majors included.</summary>
    Major,
}

/// <summary>
/// Whether the onboarding pull request also turns the merge queue on (dependency-updates PRD R25, R26): it adds
/// <c>mergeQueue: { enabled: true }</c> to <c>coverage.yml</c> unless the file already has a <c>mergeQueue</c> section.
/// </summary>
/// <remarks>⚠️ <see cref="Yes"/> is the zero value on purpose: it is R25's default, so an account written before the field
/// existed reads right without a migration.</remarks>
[SparkLookup]
public enum EDependencyOnboardingMergeQueue
{
    /// <summary>The onboarding pull request turns the merge queue on in coverage.yml.</summary>
    Yes,

    /// <summary>The onboarding pull request leaves coverage.yml's merge queue as it is.</summary>
    No,
}

/// <summary>A repository's choice for <see cref="EDependencyOnboardingMergeQueue"/>, or its account's (R26).</summary>
[SparkLookup]
public enum EDependencyOnboardingMergeQueuePolicy
{
    /// <summary>Use the account's setting.</summary>
    Inherit,

    /// <summary>The onboarding pull request turns the merge queue on in coverage.yml.</summary>
    Yes,

    /// <summary>The onboarding pull request leaves coverage.yml's merge queue as it is.</summary>
    No,
}
