using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using QnA.Entities;
using QnA.Services;

namespace QnA.Security;

/// <summary>
/// A draft question is visible to its author only (#460 M2, a row filter policy). A policy rather than
/// <c>QuestionActions.GetRowFilterAsync</c> to show the seam: it composes with the Actions class's rule
/// and SoftDelete's policy by <c>AndAlso</c>, and is pushed into the RavenDB query on every read path —
/// lists, detail, custom queries, the answers' question reference, Moderation's vote and flag targets.
/// </summary>
/// <remarks>
/// <see cref="IsVisibilityDecision"/>: this policy decides who may see a row, unlike soft deletion, so it
/// counts for the anonymous-readable validator (PRD §3.1, S5).
/// ⚠️ <c>IsDraft != true</c>, never <c>!q.IsDraft</c>: a document without the field is not a draft, and
/// RavenDB drops it from <c>!x</c> (PRD §4.1 S3).
/// </remarks>
public sealed partial class DraftQuestionPolicy : RowFilterPolicy<Question>
{
    [Inject] private readonly QnAAccess access;

    public override bool IsVisibilityDecision => true;

    public override ValueTask<Expression<Func<Question, bool>>?> GetFilterAsync(RowPolicyContext context)
    {
        var userId = access.UserId;
        Expression<Func<Question, bool>> filter = userId is null
            ? q => q.IsDraft != true
            : q => q.IsDraft != true || q.AuthorId == userId;
        return ValueTask.FromResult<Expression<Func<Question, bool>>?>(filter);
    }
}
