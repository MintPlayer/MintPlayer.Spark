using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of People from the HR module.
/// The ETL script defines which fields are replicated.
/// </summary>
[Replicated(
    SourceModule = "HR",
    SourceCollection = "People",
    EtlScript = """
        loadToPeople({
            FirstName: this.FirstName,
            LastName: this.LastName,
            Email: this.Email,
            '@metadata': {
                '@collection': 'People'
            }
        });
    """)]
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
