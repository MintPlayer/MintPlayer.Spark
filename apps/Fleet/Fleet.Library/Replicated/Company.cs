using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of HR's <c>companies</c> contract (v1, <c>HR.Contracts.CompanyV1</c>). HR
/// decides which fields are replicated and generates the ETL transform; this type only has that shape.
/// </summary>
[Replicated(
    SourceModule = "HR",
    Contract = "companies",
    SourceCollection = "Companies")]
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
