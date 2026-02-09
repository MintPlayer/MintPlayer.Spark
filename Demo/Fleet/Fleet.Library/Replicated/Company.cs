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
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int EmployeeCount { get; set; }
}
