namespace MintPlayer.Spark.Moderation;

/// <summary>
/// Moderation's configuration, bound from <c>Spark:Moderation</c> — which <c>moderation.json</c>
/// feeds as the lowest-precedence source, so appsettings, user secrets and environment variables
/// (<c>Spark__Moderation__Fraud__MaxVotesCastPerDay=10</c>) override every threshold (D14).
/// </summary>
/// <remarks>
/// Validated at startup <b>after</b> layering (<see cref="ModerationStartupCheck"/>): unknown
/// reputation event names, privilege groups that do not exist / are well-known / hold a right that
/// is not earnable, an <see cref="Earnable"/> entry on the destructive list, and a privilege with no
/// grant in <c>security.json</c> all refuse startup.
/// </remarks>
public sealed class SparkModerationOptions
{
    /// <summary>
    /// Points per reputation event (<see cref="ReputationEventKinds.Configurable"/>). Missing names
    /// keep their default; an unknown name is a startup error.
    /// </summary>
    public Dictionary<string, int> Reputation { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Entity type names (the model's <c>name</c>) on which casting a down-vote costs the voter
    /// <see cref="ReputationEventKinds.DownvoteCast"/>. Empty: every moderatable type.
    /// </summary>
    public List<string> DownvoteCastTypes { get; set; } = [];

    /// <summary>A moderator's delete of someone else's post reverses the votes it earned. Default on.</summary>
    public bool ReverseVotesOnModeratorDelete { get; set; } = true;

    /// <summary>Earnable privileges, by name. Each maps to a <c>security.json</c> group by id.</summary>
    public Dictionary<string, ModerationPrivilegeOptions> Privileges { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra actions a privilege group may hold beyond <see cref="ModerationRights.DefaultEarnable"/>
    /// (custom action names, <c>Delete</c>). Checked against <see cref="ModerationRights.NeverEarnable"/>.
    /// </summary>
    public List<string> Earnable { get; set; } = [];

    /// <summary>The ten vote-fraud measures' thresholds.</summary>
    public ModerationFraudOptions Fraud { get; set; } = new();

    /// <summary>New-account posting throttle (429 on create).</summary>
    public ModerationNewAccountOptions NewAccounts { get; set; } = new();

    /// <summary>The background jobs' schedules (NCrontab, UTC).</summary>
    public ModerationJobOptions Jobs { get; set; } = new();

    /// <summary>The points for <paramref name="kind"/>, falling back to the documented default.</summary>
    public int PointsFor(string kind) => Reputation.TryGetValue(kind, out var points) ? points : DefaultPoints(kind);

    internal static int DefaultPoints(string kind) => kind switch
    {
        ReputationEventKinds.UpvoteReceived => 10,
        ReputationEventKinds.DownvoteReceived => -2,
        ReputationEventKinds.DownvoteCast => -1,
        ReputationEventKinds.FlagUpheld => 2,
        ReputationEventKinds.FlagDeclined => 0,
        _ => 0,
    };
}

/// <summary>One earnable privilege (fraud measure 1: every privilege has a reputation, age and activity gate).</summary>
public sealed class ModerationPrivilegeOptions
{
    /// <summary>The <c>security.json</c> group the privilege confers, by id (D12).</summary>
    public Guid GroupId { get; set; }

    /// <summary>Privilege reputation required (<c>rep</c>).</summary>
    public int Rep { get; set; }

    /// <summary>Minimum account age in days.</summary>
    public int MinAccountAgeDays { get; set; }

    /// <summary>Minimum number of distinct days the account was active on.</summary>
    public int MinActiveDays { get; set; }

    /// <summary>
    /// The actions this privilege is meant to grant (<c>Vote</c>, <c>Downvote</c>, <c>Flag</c>,
    /// <c>Review</c>, <c>Edit</c> …). Used by <c>--spark-init-moderation</c> to print the
    /// <c>security.json</c> rights to add; the grants themselves live in <c>security.json</c>.
    /// </summary>
    public List<string> Grants { get; set; } = [];
}

/// <summary>Thresholds of the ten vote-fraud measures (#460 §3.12, D14). Every value is overridable.</summary>
public sealed class ModerationFraudOptions
{
    // 2. Diversity
    /// <summary>At least <c>max(DiversityMinVoters, votes / DiversityVotersDivisor)</c> distinct voters …</summary>
    public int DiversityMinVoters { get; set; } = 3;
    public int DiversityVotersDivisor { get; set; } = 5;
    /// <summary>… over at least <c>votes / DiversityDaysDivisor</c> distinct days.</summary>
    public int DiversityDaysDivisor { get; set; } = 4;

    // 3. Voter eligibility
    public int EligibleVoterMinAgeDays { get; set; } = 7;
    public int EligibleVoterMinReputation { get; set; } = 50;

    // 4. Caps
    public int MaxReputationPerRecipientPerDay { get; set; } = 200;
    public int MaxVotesCastPerDay { get; set; } = 30;
    public int MaxCreditedPairVotesPerDay { get; set; } = 3;
    public int MaxCreditedPairVotesPer30Days { get; set; } = 10;

    // 5. Delayed crediting
    public int CreditDelayHours { get; set; } = 48;

    // 6. Nightly detector (window: DetectorWindowDays)
    public int DetectorWindowDays { get; set; } = 30;
    public int SerialVotesIn24Hours { get; set; } = 5;
    public int ConcentrationMinVotes { get; set; } = 10;
    public int ConcentrationPercent { get; set; } = 50;
    public int ReciprocalMinVotesEachWay { get; set; } = 5;
    public int FastVoteSeconds { get; set; } = 60;
    public int FastVoteMinCount { get; set; } = 3;
    public int RegistrationClusterMinutes { get; set; } = 60;
    /// <summary>Domains shared by unrelated people; a shared one of these is no signal.</summary>
    public List<string> WebmailDomains { get; set; } =
    [
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com",
        "icloud.com", "me.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "mail.com",
        "yandex.com", "zoho.com", "telenet.be", "skynet.be",
    ];

    // 9. Network observations
    public int IpObservationRetentionDays { get; set; } = 90;
    public int IpKeyRotationDays { get; set; } = 30;
}

/// <summary>A new account may create at most <see cref="MaxPostsPerDay"/> moderatable entities a day for its first <see cref="AccountAgeDays"/> days.</summary>
public sealed class ModerationNewAccountOptions
{
    public int AccountAgeDays { get; set; } = 7;
    public int MaxPostsPerDay { get; set; } = 5;
}

/// <summary>Background job schedules.</summary>
public sealed class ModerationJobOptions
{
    /// <summary>Credits entries whose delay has passed and recomputes the recipients' summaries. Default every 5 minutes.</summary>
    public string CreditingSchedule { get; set; } = "*/5 * * * *";

    /// <summary>The fraud detector. Default nightly at 03:17 UTC.</summary>
    public string DetectorSchedule { get; set; } = "17 3 * * *";
}
