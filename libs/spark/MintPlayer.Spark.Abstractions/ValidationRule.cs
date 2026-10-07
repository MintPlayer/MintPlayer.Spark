namespace MintPlayer.Spark.Abstractions;

/// <summary>One validation rule on an attribute, checked by the server on save.</summary>
public sealed class ValidationRule
{
    /// <summary>
    /// The rule: <c>maxLength</c> or <c>minLength</c> (with <c>value</c>), <c>range</c> (with <c>min</c>
    /// and/or <c>max</c>), <c>regex</c> (with the pattern in <c>value</c>), <c>email</c> or <c>url</c>.
    /// Case-insensitive; an unknown rule checks nothing.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>The rule's argument: the length for <c>maxLength</c>/<c>minLength</c>, the pattern for <c>regex</c>.</summary>
    public object? Value { get; set; }

    /// <summary>For <c>range</c>: the smallest value allowed.</summary>
    public int? Min { get; set; }

    /// <summary>For <c>range</c>: the largest value allowed.</summary>
    public int? Max { get; set; }

    /// <summary>The error shown when the rule fails, as a translation key; the default is the rule's own <c>validation.*</c> message.</summary>
    public TranslatedString? Message { get; set; }
}
