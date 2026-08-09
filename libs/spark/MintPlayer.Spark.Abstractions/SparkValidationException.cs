namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Refuses a save for a reason the person at the screen has to read. Throw this from an Actions
/// class wherever the rule is a business rule rather than a shape the declarative validator can
/// express.
/// <para>
/// The endpoints translate it into the same <c>errors</c> envelope the declarative validator
/// produces, so the message lands on the field it names and the client needs no new branch.
/// Anything else thrown from an Actions class is a bug, not a refusal, and stays a 500 —
/// distinguishing the two is the whole point: a stack trace tells the operator nothing, and a
/// validation message tells the developer nothing.
/// </para>
/// </summary>
public sealed class SparkValidationException : Exception
{
    /// <summary>The attribute the message belongs to, or null when it concerns the whole object.</summary>
    public string? AttributeName { get; }

    public SparkValidationException(string message, string? attributeName = null)
        : base(message)
    {
        AttributeName = attributeName;
    }

    public ValidationError ToError() => new()
    {
        AttributeName = AttributeName ?? string.Empty,
        ErrorMessage = TranslatedString.Create(Message),
        RuleType = "custom",
    };
}
