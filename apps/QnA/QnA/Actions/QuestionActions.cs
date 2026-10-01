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
    /// row decides. <c>Delete</c>: an author deletes their own questions, a moderator any (purge is
    /// judged as <c>Delete</c>, PRD §4.1 M7). <c>Edit</c>: every signed-in user may open a question for
    /// editing, because anyone may translate it (<see cref="Question.Translations"/> is a contribution:
    /// the target's <c>Edit</c> is what lets a translator save their version) — and
    /// <see cref="GetProtectedAttributesAsync"/> keeps everything else of the question to its author
    /// and the moderators. Revert and restore are judged as <c>Edit</c> too, but both are
    /// moderator-only rights in security.json. Reading is not restricted here — drafts are
    /// <see cref="Security.DraftQuestionPolicy"/>'s.
    /// </summary>
    public override async Task<Expression<Func<Question, bool>>?> GetRowFilterAsync(string action)
    {
        if (action is not ("Edit" or "Delete"))
            return null;
        if (await access.IsModeratorAsync())
            return null;
        var userId = access.UserId;
        if (userId is null)
            return q => false;
        return action == "Edit" ? null : q => q.AuthorId == userId;
    }

    /// <summary>
    /// Everything of a question but its translations, for those who may not manage it: written only by
    /// its author or a moderator. Asked with <c>Edit</c>, the framework drops these from a save (silently,
    /// like any attribute the caller may not write), so a translator's save writes their translation and
    /// nothing else. Every property but <see cref="Question.Translations"/>, so an attribute added later
    /// is protected by default.
    /// </summary>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Question entity)
    {
        if (action != "Edit" || await access.CanManageAsync(entity.AuthorId))
            return null;
        return AuthorOnlyAttributes;
    }

    /// <summary>The question's attributes only its author or a moderator writes: all but the translations.</summary>
    public static readonly IReadOnlyCollection<string> AuthorOnlyAttributes = typeof(Question)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Select(p => p.Name)
        .Where(name => name is not (nameof(Question.Id) or nameof(Question.Translations)))
        .ToArray();

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
