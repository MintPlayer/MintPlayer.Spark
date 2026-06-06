namespace MintPlayer.Spark.Abstractions;

public sealed class ValidationError
{
    public required string AttributeName { get; set; }
    public required TranslatedString ErrorMessage { get; set; }
    public required string RuleType { get; set; }
}

public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<ValidationError> Errors { get; set; } = [];
}
