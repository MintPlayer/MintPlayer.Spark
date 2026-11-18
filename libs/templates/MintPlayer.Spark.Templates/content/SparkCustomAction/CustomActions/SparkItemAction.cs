using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;

namespace SparkNs.CustomActions;

/// <summary>
/// The SparkItem action on SparkEntity. Spark finds it by its class name without the "Action" suffix.
/// </summary>
/// <remarks>
/// It does nothing until two files name it:
/// <list type="number">
/// <item>App_Data/actions.json declares it (create the file as <c>{ }</c> if the app has none):
/// <code>"SparkItem": { "showedOn": "both", "selectionRule": "&gt;=1", "refreshOnCompleted": true }</code>
/// Its label comes from <c>actions.SparkItem.label</c> in App_Data/translations.json.</item>
/// <item>App_Data/security.json grants it, for example
/// <c>{ "key": "sparkitem", "resource": "SparkItem/SparkEntity", "groupId": "@authenticated" }</c>.</item>
/// </list>
/// </remarks>
public partial class SparkItemAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        // The rows picked in a grid, or else the object whose page the button is on. Spark has already
        // reloaded the selected rows through the caller's row rules, so these ids are ones the caller may read.
        string[] ids = args.SelectedItems.Length > 0
            ? [.. args.SelectedItems.Select(i => i.Id)]
            : args.Parent?.Id is { Length: > 0 } parentId ? [parentId] : [];

        if (ids.Length == 0)
            throw new InvalidOperationException("Select at least one SparkEntity.");

        foreach (var id in ids)
        {
            var entity = await dbAccess.GetDocumentUncheckedAsync<SparkEntity>(id)
                ?? throw new InvalidOperationException($"SparkEntity '{id}' not found.");

            // Change the entity here.

            await dbAccess.SaveDocumentUncheckedAsync(entity);
        }
    }
}
