namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Standalone model for a security group definition.
/// Extracted from SecurityConfiguration.Groups dictionary for use as a first-class entity.
/// </summary>
public sealed class SecurityGroupDefinition
{
    public Guid Id { get; set; }
    public TranslatedString? Name { get; set; }
    public TranslatedString? Comment { get; set; }
}
