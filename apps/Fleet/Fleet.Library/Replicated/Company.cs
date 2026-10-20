using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of HR's companies (warehouse §4.2.1): this class is the only declaration of what Fleet reads. The
/// replication generator renders the manifest, the script and the hash from it; HR re-renders and approves them.
/// An edit here is written back to HR and arrives back with HR's push.
/// </summary>
[Replicated(SourceCollection = "Companies", SourceModule = "HR", WriteBack = true)]
public partial class Company
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
