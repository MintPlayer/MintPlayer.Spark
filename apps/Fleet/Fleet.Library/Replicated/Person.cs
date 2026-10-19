using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of HR's <c>people</c> contract (v1, <c>HR.Contracts.PersonV1</c>). HR decides
/// which fields are replicated and generates the ETL transform; this type only has that shape.
/// </summary>
[Replicated(
    SourceModule = "HR",
    Contract = "people",
    SourceCollection = "People")]
public class Person
{
    /// <summary>Identifier of the person, replicated from HR; it matches the HR document id.</summary>
    public string? Id { get; set; }
    /// <summary>Given name of the person, replicated read-only from HR.</summary>
    public string FirstName { get; set; } = string.Empty;
    /// <summary>Family name of the person, replicated read-only from HR.</summary>
    public string LastName { get; set; } = string.Empty;
    /// <summary>Work e-mail address of the person, replicated read-only from HR.</summary>
    public string? Email { get; set; }
}
