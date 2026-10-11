using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using SeoTester.Catalogue;
using SeoTester.Entities;

namespace SeoTester.CustomActions;

/// <summary>
/// "Reset to shipped" on a catalogue entry (<c>docs/seo_analyzer_PRD.md</c> §3.1, D32; administrators): restores the
/// entry from the shipped catalogue, after which it follows the shipped text again. An entry an administrator created
/// has nothing to restore.
/// </summary>
public partial class ResetCheckDefinitionAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var definition = args.Parent?.Id is { Length: > 0 } id ? await databaseAccess.GetDocumentUncheckedAsync<CheckDefinition>(id) : null;
        if (definition is null)
            throw new SparkValidationException("Open the check first.");
        if (ShippedCatalogue.Find(definition.CheckId) is not { } shipped)
            throw new SparkValidationException(manager.GetTranslatedMessage("seo.notShipped", definition.CheckId));

        CheckDefinitionMapping.Apply(shipped, definition);
        await databaseAccess.SaveDocumentUncheckedAsync(definition);
        manager.Client.Notify(manager.GetTranslatedMessage("seo.checkReset", definition.CheckId), NotificationKind.Success);
    }
}
