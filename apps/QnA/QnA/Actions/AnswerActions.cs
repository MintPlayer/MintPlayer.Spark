using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using QnA.Entities;
using QnA.Services;
using Raven.Client.Documents.Session;

namespace QnA.Actions;

/// <summary>Answers: the same author-or-moderator row rule as questions, and the answers sub-query.</summary>
public partial class AnswerActions : DefaultPersistentObjectActions<Answer>
{
    [Inject] private readonly QnAAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <inheritdoc cref="QuestionActions.GetRowFilterAsync"/>
    public override async Task<Expression<Func<Answer, bool>>?> GetRowFilterAsync(string action)
    {
        if (action is not ("Edit" or "Delete"))
            return null;
        if (await access.IsModeratorAsync())
            return null;
        var userId = access.UserId;
        return userId is null ? a => false : a => a.AuthorId == userId;
    }

    /// <summary>
    /// The answers on a question's page. Row policies apply to custom queries like to every other read
    /// path, so deleted answers are hidden here without a word about <c>IsDeleted</c> (SoftDelete).
    /// Source: "Custom.Question_Answers".
    /// </summary>
    public Task<IQueryable<Answer>> Question_Answers(CustomQueryArgs args)
    {
        args.EnsureParent("Question");
        var questionId = args.Parent!.Id;
        return Task.FromResult<IQueryable<Answer>>(session.Query<Answer>().Where(a => a.QuestionId == questionId));
    }
}
