using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// Something a repository's managers should hear about its merge-queue rulesets, waiting for them to come in (#714 Dec5,
/// Dec11): the queue saw a required check that never reports, or GitHub refused a merge over a check's App pin, or the
/// rulesets still require one check per job. Each manager gets one bell notification per notice, linking to "Set up
/// rulesets", the next time they sign in or open the app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it waits.</b> "Manages" can only be checked for a viewer who is present: a manager of the account, or an admin of
/// the repository asked of the forge at that moment (<c>DependencyOnboardingService</c>). No list of managers exists to
/// notify in the background, so the queue writes the notice down and the bell delivers it at sign-in.
/// </para>
/// <para>
/// A plain document, not a Spark persistent object, like <see cref="MergeQueue"/>: nothing in the model, no rights. One per
/// repository and kind, <c>RulesetNotices/{provider}/{repositoryId}/{kind}</c>, so the bell reads them all with one prefix
/// load and no index.
/// </para>
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public partial class RulesetNotice
{
    public Id<RulesetNotice> Id { get; set; }

    /// <summary>The repository document id, <c>Repositories/{provider}/{id}</c>.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>The repository's full name when the notice was raised, for the notification's text.</summary>
    public string RepositoryName { get; set; } = string.Empty;

    public ERulesetNoticeKind Kind { get; set; }

    /// <summary>
    /// What the notice is about, in a form that changes when its content does (the base branch and the names): a manager
    /// told about one drift hears about the next one, and is not told twice about the same one.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The branch whose rules it concerns.</summary>
    public string BaseBranch { get; set; } = string.Empty;

    /// <summary>The required checks that never reported (<see cref="ERulesetNoticeKind.MissingCheck"/>) or that another App reports (<see cref="ERulesetNoticeKind.PinMismatch"/>).</summary>
    public List<string> Checks { get; set; } = [];

    /// <summary>For a pin mismatch: the App that reports each name now, which the setup pins it to instead (#714 Dec6).</summary>
    public Dictionary<string, long> ReportingApps { get; set; } = [];

    /// <summary>
    /// For a missing check: the names that did report on the stuck pull request's commit, which the setup offers as the
    /// summary job when the missing one was it (#714 Dec6). The setup guesses nothing from them.
    /// </summary>
    public List<string> ReportedOnHead { get; set; } = [];

    /// <summary>The pull request the queue saw it on; null for the summary-job offer.</summary>
    public int? PullRequest { get; set; }

    public DateTime RaisedAtUtc { get; set; }

    /// <summary>When it stopped applying: the setup ran, or the queue found the rules fixed. A resolved notice is not delivered.</summary>
    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>The users told about this <see cref="Key"/>; emptied when the key changes.</summary>
    public List<string> NotifiedUsers { get; set; } = [];

    /// <summary>Still to be delivered: not resolved. A method, so it is not stored.</summary>
    public bool IsOpen() => ResolvedAtUtc is null;

    /// <summary>The prefix of every notice id, for the bell's one load.</summary>
    public const string Prefix = "RulesetNotices/";

    /// <summary><c>RulesetNotices/{provider}/{repositoryId}/{kind}</c>.</summary>
    public static string DocumentId(string repositoryId, ERulesetNoticeKind kind)
        => $"{Prefix}{MergeQueue.RepositorySuffix(repositoryId)}/{kind switch
        {
            ERulesetNoticeKind.MissingCheck => "missing-check",
            ERulesetNoticeKind.PinMismatch => "pin-mismatch",
            _ => "summary-job",
        }}";
}

public enum ERulesetNoticeKind
{
    /// <summary>A queued pull request waited for a required check that never reported (#714 Dec5).</summary>
    MissingCheck,

    /// <summary>GitHub refused a merge because a required check is pinned to an App other than the one that reports it (#714 Dec4, Dec5).</summary>
    PinMismatch,

    /// <summary>The rulesets require more than one check the App does not report itself: offered the switch to one summary job, once (#714 Dec11).</summary>
    SummaryJob,
}
