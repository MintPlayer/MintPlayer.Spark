using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using QnA.Entities;
using Raven.Client.Documents.Session;

namespace QnA.Interceptors;

/// <summary>
/// A closed question takes no new answers (#460 M2, an interceptor). An interceptor rather than a
/// rule on <c>AnswerActions</c> because it must hold on every write path — the PO endpoints, a custom
/// action saving through <c>IDatabaseAccess</c>, a revert — and interceptors run in <c>DatabaseAccess</c>,
/// around all of them (D1).
/// </summary>
/// <remarks>
/// ⚠️ Core does not row-check references on save (a documented D1 gap), so this is also the place that
/// refuses an answer pointing at a question that does not exist. It deliberately answers a question the
/// caller cannot see (a draft) the same way as a missing one, so the refusal reveals nothing.
/// </remarks>
public sealed partial class ClosedQuestionInterceptor : IBeforeSave
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly Services.QnAAccess access;

    public bool AppliesTo(Type entityType) => entityType == typeof(Answer);

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        // Only a new answer: editing an answer on a question closed since is still allowed.
        if (!context.IsNew || context.IsSystemContext || context.Entity is not Answer answer)
            return;

        var question = string.IsNullOrEmpty(answer.QuestionId) ? null : await session.LoadAsync<Question>(answer.QuestionId);
        if (question is null || question.IsDeleted || (question.IsDraft && question.AuthorId != access.UserId))
            throw new SparkValidationException("Choose the question this answers.", nameof(Answer.QuestionId));
        if (question.IsClosed)
            throw new SparkValidationException("This question is closed and takes no new answers.", nameof(Answer.QuestionId));
    }
}
