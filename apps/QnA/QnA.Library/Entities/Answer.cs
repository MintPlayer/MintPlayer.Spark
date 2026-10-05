using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;

namespace QnA.Entities;

/// <summary>An answer to a <see cref="Question"/>. Moderatable, soft-deletable and audited like the question.</summary>
public class Answer : IModeratable, ISoftDeletable, IAuditable
{
    /// <summary>Unique identifier of the answer, assigned automatically when it is posted.</summary>
    public string? Id { get; set; }

    /// <summary>The question this answers. A closed question refuses new answers.</summary>
    [Reference(typeof(Question))]
    public string? QuestionId { get; set; }

    /// <summary>The answer itself.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>The vote widget's slot on the page; the score lives in <c>ModerationTallies/{id}</c>.</summary>
    public int Votes { get; set; }

    /// <summary>Who answered. Stamped by Moderation on create, never changed afterwards.</summary>
    public string? AuthorId { get; set; }

    /// <summary>When the answer was posted. Stamped by Moderation.</summary>
    public DateTimeOffset? PostedAt { get; set; }

    /// <summary>Whether the answer was deleted. Deleted answers are hidden everywhere until restored.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>When the answer was deleted.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Who deleted the answer (a user id).</summary>
    public string? DeletedBy { get; set; }

    /// <summary>Why the answer was deleted, when a reason was given.</summary>
    public string? DeleteReason { get; set; }

    /// <summary>Who created the answer (a user id). Stamped by History.</summary>
    /// <remarks>
    /// A reference, so the page shows the user's name (SparkUser's <c>{UserName}</c> breadcrumb) rather
    /// than an id. Visitors may read <c>SparkUser</c>, but <c>security.json</c> denies them every user
    /// attribute except <c>UserName</c> (#264, G-Q15).
    /// </remarks>
    [Reference(typeof(SparkUser))]
    public string? CreatedBy { get; set; }

    /// <summary>When the answer was created. Stamped by History.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Who changed the answer last (a user id). Stamped by History.</summary>
    [Reference(typeof(SparkUser))]
    public string? ModifiedBy { get; set; }

    /// <summary>When the answer was changed last. Stamped by History.</summary>
    public DateTimeOffset? ModifiedAt { get; set; }
}
