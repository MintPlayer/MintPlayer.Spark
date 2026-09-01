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
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
}
