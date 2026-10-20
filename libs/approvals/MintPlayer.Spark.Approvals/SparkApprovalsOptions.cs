using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Approvals;

/// <summary><c>Spark:Approvals</c> (commerce PRD §4.13). Code given to <c>AddApprovals(o => …)</c> runs after the section.</summary>
public sealed class SparkApprovalsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Approvals";

    /// <summary>
    /// The persistent-object types whose saves become document proposals, by type name:
    /// <c>"Types": { "Company": { "Create": true, "Modify": true }, "Profession": { "Create": true } }</c>.
    /// Grant <c>ApproveProposal/&lt;Type&gt;</c> to the approvers in <c>security.json</c>; startup is refused while a
    /// listed type is unknown.
    /// </summary>
    public Dictionary<string, SparkApprovalTypeOptions> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long deciders have before a request expires. Default 30 days; a submission may set its own.</summary>
    public TimeSpan DecideWithin { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long a request is kept after it is decided (or after its deadline). Default 365 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>The sweep that expires overdue requests and tells the deciders of requests raised inside another session. Every minute by default.</summary>
    public string SweepSchedule { get; set; } = "* * * * *";

    /// <summary>The fallback mail templates (no <see cref="IApprovalNotifier"/> registered); an app's own file of the same name wins.</summary>
    public SparkApprovalMailTemplates Mail { get; set; } = new();
}

/// <summary>Which saves of one type wait for approval.</summary>
public sealed class SparkApprovalTypeOptions
{
    /// <summary>A new row is proposed; it does not exist (and no query shows it) until it is approved.</summary>
    public bool Create { get; set; }

    /// <summary>An edit is proposed; the stored row stays as it is until the edit is approved.</summary>
    public bool Modify { get; set; }
}

/// <summary>The mail templates of the fallback notices.</summary>
public sealed class SparkApprovalMailTemplates
{
    public string Submitted { get; set; } = "SparkApprovals/Submitted";

    public string Decided { get; set; } = "SparkApprovals/Decided";
}
