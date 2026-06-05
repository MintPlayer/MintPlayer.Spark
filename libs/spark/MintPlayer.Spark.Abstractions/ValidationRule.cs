namespace MintPlayer.Spark.Abstractions;

public sealed class ValidationRule
{
    public required string Type { get; set; }
    public object? Value { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public TranslatedString? Message { get; set; }
}
