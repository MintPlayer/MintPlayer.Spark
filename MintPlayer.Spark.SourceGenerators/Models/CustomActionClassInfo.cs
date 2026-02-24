namespace MintPlayer.Spark.SourceGenerators.Models;

public class CustomActionClassInfo
{
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// The action name derived from the class name (with optional "Action" suffix stripped).
    /// </summary>
    public string ActionName { get; set; } = string.Empty;
}
