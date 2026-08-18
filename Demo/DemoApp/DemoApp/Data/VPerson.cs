using DemoApp.Indexes;
using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Data;

/// <summary>
/// View model for Person used by the People_Overview RavenDB index.
/// Contains computed/projected properties optimized for list views.
/// </summary>
[FromIndex(typeof(People_Overview))]
public class VPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    [Reference(typeof(Company))]
    public string? Company { get; set; }
    /// <summary>
    /// Sort companion for <c>FullName</c>. The base field is analyzed for search, which tokenizes it, so ordering
    /// on it is meaningless for a value containing spaces. This carries the same value with no indexing
    /// declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string FullNameSort { get; set; } = string.Empty;

    /// <summary>
    /// Sort companion for <c>Email</c>. The base field is analyzed for search, which tokenizes it, so ordering
    /// on it is meaningless for a value containing spaces. This carries the same value with no indexing
    /// declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string EmailSort { get; set; } = string.Empty;
}
