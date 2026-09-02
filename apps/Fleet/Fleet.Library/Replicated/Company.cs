using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of Companies from the HR module.
/// The ETL script defines which fields are replicated.
/// </summary>
[Replicated(
    SourceModule = "HR",
    SourceCollection = "Companies",
    EtlScript = """
        loadToCompanies({
            Name: this.Name,
            Website: this.Website,
            EmployeeCount: this.EmployeeCount,
            '@metadata': {
                '@collection': 'Companies'
            }
        });
    """)]
public class Company
{
    /// <summary>Identifier of the company, replicated from HR; it matches the HR document id.</summary>
    public string? Id { get; set; }
    /// <summary>Legal or trading name of the company, replicated read-only from HR.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Public website address of the company, replicated read-only from HR.</summary>
    public string? Website { get; set; }
    /// <summary>Number of employees the company has, as maintained in HR.</summary>
    public int EmployeeCount { get; set; }
}
