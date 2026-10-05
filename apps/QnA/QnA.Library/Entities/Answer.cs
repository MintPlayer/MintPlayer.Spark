using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.SoftDelete;

namespace QnA.Entities;

/// <summary>An answer to a <see cref="Question"/>. Moderatable, soft-deletable and audited like the question.</summary>
/// <remarks>The ten moderation, soft-delete and audit members are generated from the interfaces (#271), as on <see cref="Question"/>.</remarks>
public partial class Answer : IModeratable, ISoftDeletable, IAuditable
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
}
