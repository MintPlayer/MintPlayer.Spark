using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using QnA.Entities;
using QnA.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace QnA.Actions;

/// <summary>
/// Questions: who may change one (a row rule), which actions a question offers (#460 D13), and the
/// state change the Close / Reopen actions carry into the save.
/// </summary>
public partial class QuestionActions : DefaultPersistentObjectActions<Question>
{
    [Inject] private readonly QnAAccess access;
    [Inject] private readonly QuestionStateChanges stateChanges;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// Everyone signed in holds <c>Edit/Question</c> and <c>Delete/Question</c> (security.json), so the
    /// row decides: an author changes their own questions, a moderator any. Revert and restore are judged
    /// as <c>Edit</c> here and purge as <c>Delete</c> (PRD §4.1 M7), which is what keeps them to the same
    /// people. Reading is not restricted here — drafts are <see cref="Security.DraftQuestionPolicy"/>'s.
    /// </summary>
    public override async Task<Expression<Func<Question, bool>>?> GetRowFilterAsync(string action)
    {
        if (action is not ("Edit" or "Delete"))
            return null;
        if (await access.IsModeratorAsync())
            return null;
        var userId = access.UserId;
        return userId is null ? q => false : q => q.AuthorId == userId;
    }

    /// <summary>
    /// The single source of truth for what a question offers (#460 D13, M3), from the stored question,
    /// the caller and stored answers only — so the page load and the submit agree:
    /// <list type="bullet">
    ///   <item><c>CloseQuestion</c> / <c>ReopenQuestion</c>: only the author or a moderator, and only the one that changes something.</item>
    ///   <item><c>Delete</c>: withheld from the author while the question has answers — other people's work hangs on it. Moderators still delete.</item>
    /// </list>
    /// </summary>
    public override async Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.TargetKind != DisableActionsTargetKind.PersistentObject || context.Entity is not Question question)
            return;

        var canManage = await access.CanManageAsync(question.AuthorId);
        if (!canManage || question.IsClosed)
            target.DisableActions("CloseQuestion");
        if (!canManage || !question.IsClosed)
            target.DisableActions("ReopenQuestion");

        if (question.Id is { } id && !await access.IsModeratorAsync() && await HasLiveAnswersAsync(id))
            target.DisableActions("Delete");
    }

    /// <summary>Applies a pending Close / Reopen (<see cref="QuestionStateChanges"/>) to the entity being saved.</summary>
    public override Task OnBeforeSaveAsync(PersistentObject obj, Question entity)
    {
        if (entity.Id is { } id && stateChanges.TryTakeClosed(id, out var isClosed))
            entity.IsClosed = isClosed;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether any live answer points at the question. `!= true`, never `!a.IsDeleted`: a document
    /// without the field must count as live (PRD §4.1 S3).
    /// </summary>
    /// <remarks>
    /// A query, so an index: waited on (bounded) so that an answer posted a moment ago counts, and the
    /// load and the submit agree (D13). An index still stale after the bound fails closed — Delete is
    /// withheld, which a reload can lift; offering it wrongly would let the author delete answered work.
    /// </remarks>
    private async Task<bool> HasLiveAnswersAsync(string questionId)
    {
        try
        {
            return await session.Query<Answer>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(3)))
                .AnyAsync(a => a.QuestionId == questionId && a.IsDeleted != true);
        }
        catch (Exception ex) when (ex is TimeoutException or Raven.Client.Exceptions.RavenTimeoutException)
        {
            return true;
        }
    }
}
