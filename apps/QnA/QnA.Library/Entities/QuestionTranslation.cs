using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Moderation;

namespace QnA.Entities;

/// <summary>
/// A question in another language. One row per language and script (<c>sr/Latn</c> and <c>sr/Cyrl</c>
/// are two rows); the row shows the latest translator's version of it.
/// </summary>
public partial class QuestionTranslation
{
    /// <summary>The language, as an ISO 639 code: <c>nl</c>, <c>fr</c>, <c>sr</c>.</summary>
    [ContributionSlot] public string Language { get; set; } = string.Empty;

    /// <summary>The script, as an ISO 15924 code: <c>Latn</c>, <c>Cyrl</c>.</summary>
    [ContributionSlot] public string Script { get; set; } = string.Empty;

    /// <summary>The title in this language.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The question itself in this language.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// A translator's version is a post of its own for Moderation: it can be flagged, and the review queue
/// and the lock work on it. The author is the translator and the date the version's, both owned by the
/// Contributions runtime — so the members are read-only views (a moderator's edit cannot reassign them).
/// </summary>
public partial class QuestionTranslationsContribution : IModeratable
{
    string? IModeratable.AuthorId
    {
        get => ContributorId;
        set { }
    }

    DateTimeOffset? IModeratable.PostedAt
    {
        get => UpdatedAt == default ? null : new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc));
        set { }
    }
}
