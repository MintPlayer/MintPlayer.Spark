using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using QnA.Services;

namespace QnA.CustomActions;

/// <summary>
/// Closes a question: it takes no new answers (<see cref="Interceptors.ClosedQuestionInterceptor"/>).
/// Offered only to the author or a moderator of an open question (<c>QuestionActions.OnDisableActionsAsync</c>),
/// and refused with 403 at submit otherwise — the framework asks the same hook again.
/// </summary>
public partial class CloseQuestionAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly QuestionStateChanges stateChanges;

    public override Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
        => QuestionClosing.SetAsync(databaseAccess, stateChanges, args, isClosed: true);
}

/// <summary>Reopens a closed question. The mirror of <see cref="CloseQuestionAction"/>.</summary>
public partial class ReopenQuestionAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly QuestionStateChanges stateChanges;

    public override Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
        => QuestionClosing.SetAsync(databaseAccess, stateChanges, args, isClosed: false);
}

internal static class QuestionClosing
{
    /// <summary>
    /// Saves the question through <see cref="IDatabaseAccess"/> — the full pipeline, so a locked question
    /// stays closed or open (Moderation answers 400), the change is stamped and becomes a revision.
    /// </summary>
    public static async Task SetAsync(IDatabaseAccess databaseAccess, QuestionStateChanges stateChanges, CustomActionArgs args, bool isClosed)
    {
        var parent = args.Parent ?? throw new SparkValidationException("Open the question to close or reopen it.");
        var question = await databaseAccess.GetPersistentObjectAsync(parent.ObjectTypeId, parent.Id!)
            ?? throw new SparkValidationException("This question no longer exists.");

        stateChanges.SetClosed(question.Id!, isClosed);
        await databaseAccess.SavePersistentObjectAsync(question);
    }
}
