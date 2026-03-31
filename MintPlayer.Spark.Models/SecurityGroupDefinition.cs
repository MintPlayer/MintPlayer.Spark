namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Standalone model for a security group definition.
/// Extracted from SecurityConfiguration.Groups dictionary for use as a first-class entity.
/// </summary>
public class SecurityGroupDefinition
{
    /// <summary>
    /// Document/entity identifier.
    /// </summary>
    public string? Id { get; set; }

    public TranslatedString? Name { get; set; }
    public TranslatedString? Comment { get; set; }
}
