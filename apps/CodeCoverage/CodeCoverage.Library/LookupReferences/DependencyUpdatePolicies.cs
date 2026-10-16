using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

// The dependency updater's owner settings (docs/code-coverage/dependency-updates-PRD.md §4.1, as §13.10 keeps them: the
// feature switch, the schedule, the cooldown and the bisect budget; thresholds moved to coverage.yml, R6). Each account
// value has a zero value that IS its default, so an account written before the field existed needs no migration to read
// right; the backfill migration only makes the stored documents queryable (M_202610161200).

/// <summary>Whether a <c>Repository</c> takes part in dependency updates, or defers to its account (PRD §4.1, G2).</summary>
public enum EDependencyUpdatesPolicy
{
    /// <summary>Use the account's setting. The default for every repository.</summary>
    Inherit,

    Enabled,

    Disabled,
}

/// <summary>How often the dependency updater recomputes the rolling pull request (Q8).</summary>
/// <remarks>⚠️ <see cref="Weekly"/> is the zero value on purpose: it is Q8's default.</remarks>
public enum EDependencyScheduleFrequency
{
    Weekly,

    Daily,

    Monthly,
}

/// <summary>The weekday of a weekly slot (Q8); ignored for daily. A monthly slot is the first such weekday of the month.</summary>
/// <remarks>⚠️ <see cref="Monday"/> is the zero value on purpose: it is Q8's default.</remarks>
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
public enum EDependencySchedulePolicy
{
    /// <summary>Use the account's frequency, weekday and hour. The default.</summary>
    Inherit,

    /// <summary>Use this repository's own frequency, weekday and hour.</summary>
    Override,
}

/// <summary>How old a third-party release must be before it is planned (Q16, §7.6). Own packages are exempt.</summary>
/// <remarks>⚠️ <see cref="ThreeDays"/> is the zero value on purpose: it is Q16's default.</remarks>
public enum EDependencyCooldown
{
    ThreeDays,

    None,

    OneDay,

    SevenDays,
}

/// <summary>The cooldown on a <c>Repository</c>, which may defer to its account.</summary>
public enum EDependencyCooldownPolicy
{
    Inherit,

    None,

    OneDay,

    ThreeDays,

    SevenDays,
}

/// <summary>How many CI runs the updater may spend bisecting a red rolling pull request (Q12, §7.4).</summary>
/// <remarks>⚠️ <see cref="Two"/> is the zero value on purpose: it is Q12's default.</remarks>
public enum EDependencyBisectBudget
{
    Two,

    Off,

    One,

    Four,

    Eight,
}

/// <summary>The bisect budget on a <c>Repository</c>, which may defer to its account.</summary>
public enum EDependencyBisectBudgetPolicy
{
    Inherit,

    Off,

    One,

    Two,

    Four,

    Eight,
}

/// <summary>
/// The largest update that is planned (and auto-merged) without an approval (R6): <c>never &lt; patch &lt; minor &lt; major</c>.
/// On an account's <c>DependencyRule</c> it is the rule's ceiling.
/// </summary>
/// <remarks>The order of the members IS the order of the levels; code compares them.</remarks>
public enum EDependencyThreshold
{
    Never,

    Patch,

    Minor,

    Major,
}

public sealed class DependencyUpdatesPolicy : TransientLookupReference<EDependencyUpdatesPolicy>
{
    private DependencyUpdatesPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyUpdatesPolicy> Items { get; } =
    [
        new DependencyUpdatesPolicy
        {
            Key = EDependencyUpdatesPolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new DependencyUpdatesPolicy
        {
            Key = EDependencyUpdatesPolicy.Enabled,
            Description = "Keep a rolling dependency-update pull request in this repository",
            Values = _TS("Enabled", "Activé", "Ingeschakeld"),
        },
        new DependencyUpdatesPolicy
        {
            Key = EDependencyUpdatesPolicy.Disabled,
            Description = "No dependency updates in this repository, whatever the account setting says",
            Values = _TS("Disabled", "Désactivé", "Uitgeschakeld"),
        },
    ];
}

public sealed class DependencyScheduleFrequency : TransientLookupReference<EDependencyScheduleFrequency>
{
    private DependencyScheduleFrequency() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyScheduleFrequency> Items { get; } =
    [
        new DependencyScheduleFrequency { Key = EDependencyScheduleFrequency.Daily, Description = "Every day", Values = _TS("Daily", "Quotidien", "Dagelijks") },
        new DependencyScheduleFrequency { Key = EDependencyScheduleFrequency.Weekly, Description = "Once a week, on the chosen weekday", Values = _TS("Weekly", "Hebdomadaire", "Wekelijks") },
        new DependencyScheduleFrequency { Key = EDependencyScheduleFrequency.Monthly, Description = "Once a month, on the first chosen weekday", Values = _TS("Monthly", "Mensuel", "Maandelijks") },
    ];
}

public sealed class DependencyWeekday : TransientLookupReference<EDependencyWeekday>
{
    private DependencyWeekday() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyWeekday> Items { get; } =
    [
        new DependencyWeekday { Key = EDependencyWeekday.Monday, Values = _TS("Monday", "Lundi", "Maandag") },
        new DependencyWeekday { Key = EDependencyWeekday.Tuesday, Values = _TS("Tuesday", "Mardi", "Dinsdag") },
        new DependencyWeekday { Key = EDependencyWeekday.Wednesday, Values = _TS("Wednesday", "Mercredi", "Woensdag") },
        new DependencyWeekday { Key = EDependencyWeekday.Thursday, Values = _TS("Thursday", "Jeudi", "Donderdag") },
        new DependencyWeekday { Key = EDependencyWeekday.Friday, Values = _TS("Friday", "Vendredi", "Vrijdag") },
        new DependencyWeekday { Key = EDependencyWeekday.Saturday, Values = _TS("Saturday", "Samedi", "Zaterdag") },
        new DependencyWeekday { Key = EDependencyWeekday.Sunday, Values = _TS("Sunday", "Dimanche", "Zondag") },
    ];
}

public sealed class DependencySchedulePolicy : TransientLookupReference<EDependencySchedulePolicy>
{
    private DependencySchedulePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencySchedulePolicy> Items { get; } =
    [
        new DependencySchedulePolicy
        {
            Key = EDependencySchedulePolicy.Inherit,
            Description = "Use the account's schedule",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new DependencySchedulePolicy
        {
            Key = EDependencySchedulePolicy.Override,
            Description = "Use this repository's own frequency, weekday and hour",
            Values = _TS("Own schedule", "Calendrier propre", "Eigen planning"),
        },
    ];
}

public sealed class DependencyCooldown : TransientLookupReference<EDependencyCooldown>
{
    private DependencyCooldown() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyCooldown> Items { get; } =
    [
        new DependencyCooldown { Key = EDependencyCooldown.None, Description = "Plan a release as soon as it is seen", Values = _TS("None", "Aucun", "Geen") },
        new DependencyCooldown { Key = EDependencyCooldown.OneDay, Description = "Wait one day after a release", Values = _TS("1 day", "1 jour", "1 dag") },
        new DependencyCooldown { Key = EDependencyCooldown.ThreeDays, Description = "Wait three days after a release", Values = _TS("3 days", "3 jours", "3 dagen") },
        new DependencyCooldown { Key = EDependencyCooldown.SevenDays, Description = "Wait seven days after a release", Values = _TS("7 days", "7 jours", "7 dagen") },
    ];
}

public sealed class DependencyCooldownPolicy : TransientLookupReference<EDependencyCooldownPolicy>
{
    private DependencyCooldownPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyCooldownPolicy> Items { get; } =
    [
        new DependencyCooldownPolicy
        {
            Key = EDependencyCooldownPolicy.Inherit,
            Description = "Use the account's cooldown",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new DependencyCooldownPolicy { Key = EDependencyCooldownPolicy.None, Description = "Plan a release as soon as it is seen", Values = _TS("None", "Aucun", "Geen") },
        new DependencyCooldownPolicy { Key = EDependencyCooldownPolicy.OneDay, Description = "Wait one day after a release", Values = _TS("1 day", "1 jour", "1 dag") },
        new DependencyCooldownPolicy { Key = EDependencyCooldownPolicy.ThreeDays, Description = "Wait three days after a release", Values = _TS("3 days", "3 jours", "3 dagen") },
        new DependencyCooldownPolicy { Key = EDependencyCooldownPolicy.SevenDays, Description = "Wait seven days after a release", Values = _TS("7 days", "7 jours", "7 dagen") },
    ];
}

public sealed class DependencyBisectBudget : TransientLookupReference<EDependencyBisectBudget>
{
    private DependencyBisectBudget() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyBisectBudget> Items { get; } =
    [
        new DependencyBisectBudget { Key = EDependencyBisectBudget.Off, Description = "Never bisect a red pull request", Values = _TS("Off", "Désactivé", "Uit") },
        new DependencyBisectBudget { Key = EDependencyBisectBudget.One, Description = "At most one CI run", Values = _TS("1 CI run", "1 exécution CI", "1 CI-run") },
        new DependencyBisectBudget { Key = EDependencyBisectBudget.Two, Description = "At most two CI runs", Values = _TS("2 CI runs", "2 exécutions CI", "2 CI-runs") },
        new DependencyBisectBudget { Key = EDependencyBisectBudget.Four, Description = "At most four CI runs", Values = _TS("4 CI runs", "4 exécutions CI", "4 CI-runs") },
        new DependencyBisectBudget { Key = EDependencyBisectBudget.Eight, Description = "At most eight CI runs", Values = _TS("8 CI runs", "8 exécutions CI", "8 CI-runs") },
    ];
}

public sealed class DependencyBisectBudgetPolicy : TransientLookupReference<EDependencyBisectBudgetPolicy>
{
    private DependencyBisectBudgetPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyBisectBudgetPolicy> Items { get; } =
    [
        new DependencyBisectBudgetPolicy
        {
            Key = EDependencyBisectBudgetPolicy.Inherit,
            Description = "Use the account's bisect budget",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new DependencyBisectBudgetPolicy { Key = EDependencyBisectBudgetPolicy.Off, Description = "Never bisect a red pull request", Values = _TS("Off", "Désactivé", "Uit") },
        new DependencyBisectBudgetPolicy { Key = EDependencyBisectBudgetPolicy.One, Description = "At most one CI run", Values = _TS("1 CI run", "1 exécution CI", "1 CI-run") },
        new DependencyBisectBudgetPolicy { Key = EDependencyBisectBudgetPolicy.Two, Description = "At most two CI runs", Values = _TS("2 CI runs", "2 exécutions CI", "2 CI-runs") },
        new DependencyBisectBudgetPolicy { Key = EDependencyBisectBudgetPolicy.Four, Description = "At most four CI runs", Values = _TS("4 CI runs", "4 exécutions CI", "4 CI-runs") },
        new DependencyBisectBudgetPolicy { Key = EDependencyBisectBudgetPolicy.Eight, Description = "At most eight CI runs", Values = _TS("8 CI runs", "8 exécutions CI", "8 CI-runs") },
    ];
}

/// <summary>
/// Whether the onboarding pull request also turns the merge queue on (dependency-updates PRD R25, R26): it adds
/// <c>mergeQueue: { enabled: true }</c> to <c>coverage.yml</c> unless the file already has a <c>mergeQueue</c> section.
/// </summary>
/// <remarks>⚠️ <see cref="Yes"/> is the zero value on purpose: it is R25's default, so an account written before the field
/// existed reads right without a migration.</remarks>
public enum EDependencyOnboardingMergeQueue
{
    Yes,

    No,
}

/// <summary>A repository's choice for <see cref="EDependencyOnboardingMergeQueue"/>, or its account's (R26).</summary>
public enum EDependencyOnboardingMergeQueuePolicy
{
    Inherit,

    Yes,

    No,
}

public sealed class DependencyOnboardingMergeQueue : TransientLookupReference<EDependencyOnboardingMergeQueue>
{
    private DependencyOnboardingMergeQueue() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyOnboardingMergeQueue> Items { get; } =
    [
        new DependencyOnboardingMergeQueue
        {
            Key = EDependencyOnboardingMergeQueue.Yes,
            Description = "The onboarding pull request turns the merge queue on in coverage.yml",
            Values = _TS("Yes", "Oui", "Ja"),
        },
        new DependencyOnboardingMergeQueue
        {
            Key = EDependencyOnboardingMergeQueue.No,
            Description = "The onboarding pull request leaves coverage.yml's merge queue as it is",
            Values = _TS("No", "Non", "Nee"),
        },
    ];
}

public sealed class DependencyOnboardingMergeQueuePolicy : TransientLookupReference<EDependencyOnboardingMergeQueuePolicy>
{
    private DependencyOnboardingMergeQueuePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyOnboardingMergeQueuePolicy> Items { get; } =
    [
        new DependencyOnboardingMergeQueuePolicy
        {
            Key = EDependencyOnboardingMergeQueuePolicy.Inherit,
            Description = "Use the account's setting",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new DependencyOnboardingMergeQueuePolicy
        {
            Key = EDependencyOnboardingMergeQueuePolicy.Yes,
            Description = "The onboarding pull request turns the merge queue on in coverage.yml",
            Values = _TS("Yes", "Oui", "Ja"),
        },
        new DependencyOnboardingMergeQueuePolicy
        {
            Key = EDependencyOnboardingMergeQueuePolicy.No,
            Description = "The onboarding pull request leaves coverage.yml's merge queue as it is",
            Values = _TS("No", "Non", "Nee"),
        },
    ];
}

public sealed class DependencyThreshold : TransientLookupReference<EDependencyThreshold>
{
    private DependencyThreshold() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<DependencyThreshold> Items { get; } =
    [
        new DependencyThreshold { Key = EDependencyThreshold.Never, Description = "Every update needs an approval", Values = _TS("Never", "Jamais", "Nooit") },
        new DependencyThreshold { Key = EDependencyThreshold.Patch, Description = "Patch updates at most", Values = _TS("Patch", "Correctif", "Patch") },
        new DependencyThreshold { Key = EDependencyThreshold.Minor, Description = "Minor and patch updates", Values = _TS("Minor", "Mineure", "Minor") },
        new DependencyThreshold { Key = EDependencyThreshold.Major, Description = "Any update, majors included", Values = _TS("Major", "Majeure", "Major") },
    ];
}
