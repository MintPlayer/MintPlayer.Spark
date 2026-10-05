using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;

namespace QnA.Entities;

/// <summary>
/// A question. Moderatable (votes, flags, locks), soft-deletable (restore / purge) and audited
/// (revisions, revert) — the three #460 packages on one entity, each through its marker interface.
/// </summary>
/// <remarks>
/// The ten moderation, soft-delete and audit members are generated from the three interfaces (#271);
/// <c>CreatedBy</c>, <c>ModifiedBy</c>, <c>DeletedBy</c> and <c>AuthorId</c> are references to <c>SparkUser</c>, so
/// the page shows the user's <c>{UserName}</c>. Visitors may read <c>SparkUser</c>, but <c>security.json</c>
/// denies them every user attribute except <c>UserName</c> (#264, G-Q15).
/// </remarks>
public partial class Question : IModeratable, ISoftDeletable, IAuditable
{
    /// <summary>Unique identifier of the question, assigned automatically when it is posted.</summary>
    public string? Id { get; set; }

    /// <summary>One line that states the question.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The question itself: what you tried and what you expected.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Comma-separated tags, stored lower-case and without duplicates (at most five).</summary>
    public string? Tags { get; set; }

    /// <summary>A draft is visible to its author only, until it is published by clearing this box.</summary>
    public bool IsDraft { get; set; }

    /// <summary>A closed question takes no new answers. Changed with the Close and Reopen actions.</summary>
    public bool IsClosed { get; set; }

    /// <summary>
    /// The vote widget's slot on the page. The score itself is never stored here: a vote writes
    /// <c>ModerationTallies/{id}</c>, so voting does not move this document's etag while its author edits it.
    /// </summary>
    public int Votes { get; set; }

    /// <summary>
    /// The question in other languages, written by anyone signed in. Each language and script holds one
    /// version per translator; the latest one is shown, and every version stays in the history.
    /// </summary>
    /// <remarks>
    /// A contribution (MintPlayer.Spark.Contributions): stored in <c>QuestionTranslationsContribution</c>
    /// and <c>QuestionTranslationsCurrent</c> documents, never on the question, so a translation does
    /// not move the question's etag or make a revision of it.
    /// </remarks>
    [Contribution(Attribution = ContributionAttribution.Contributor | ContributionAttribution.UpdatedAt | ContributionAttribution.History)]
    [Newtonsoft.Json.JsonIgnore]
    public List<QuestionTranslation> Translations { get; set; } = [];
}
