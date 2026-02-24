namespace MintPlayer.Spark.Models;

/// <summary>
/// Root model for the customActions.json configuration file.
/// Maps action name to its metadata definition.
/// </summary>
public class CustomActionsConfiguration : Dictionary<string, CustomActionDefinition>
{
}
