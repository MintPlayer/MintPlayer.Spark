using MintPlayer.Spark.Replication.Abstractions;

namespace Fleet.Replicated;

/// <summary>
/// A read-only copy of HR's people (warehouse §4.2.1): this class is the only declaration of what Fleet reads. The
/// replication generator renders the manifest, the script and the hash from it; HR re-renders and approves them.
/// An edit here is written back to HR and arrives back with HR's push.
/// </summary>
[Replicated(SourceCollection = "People", SourceModule = "HR", WriteBack = true)]
public partial class Person
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
