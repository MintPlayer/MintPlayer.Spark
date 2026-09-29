using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using QnA.Entities;

namespace QnA.CustomActions;

/// <summary>
/// Duplicates one answer under the same question — the <c>=1</c> action of the Answers sub-query
/// (#460 M15): enabled only with exactly one row ticked, and offered in every row's <c>⋮</c> menu.
/// </summary>
/// <remarks>
/// The framework has already gated the row (it was re-materialized through the Read gate, and the
/// row rule was asked about <c>DuplicateAnswer</c>), so the source is read directly. The copy is saved
/// through <see cref="IDatabaseAccess"/> — the full pipeline — so a closed or locked question refuses
/// it (400) and the copy is stamped and authored like any new answer.
/// </remarks>
public partial class DuplicateAnswerAction : SparkCustomAction
{
    /// <summary>The Answer type's id in <c>App_Data/Model/Answer.json</c>.</summary>
    internal static readonly Guid AnswerTypeId = Guid.Parse("39342297-cb55-4adb-b132-3c5e9b9fddcf");

    [Inject] private readonly IDatabaseAccess databaseAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var row = args.SelectedItems.Length == 1
            ? args.SelectedItems[0]
            : throw new SparkValidationException("Select exactly one answer to duplicate.");

        var source = await databaseAccess.GetDocumentUncheckedAsync<Answer>(row.Id)
            ?? throw new SparkValidationException("This answer no longer exists.");

        var copy = new PersistentObject
        {
            Name = "Answer",
            ObjectTypeId = AnswerTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = nameof(Answer.QuestionId), Value = source.QuestionId, IsValueChanged = true, DataType = "Reference" },
                new PersistentObjectAttribute { Name = nameof(Answer.Body), Value = source.Body, IsValueChanged = true },
            ],
        };

        await databaseAccess.SavePersistentObjectAsync(copy);
    }
}
