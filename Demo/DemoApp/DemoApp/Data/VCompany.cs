using DemoApp.Indexes;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Data;

[FromIndex(typeof(Companies_Overview))]
public class VCompany
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
    /// <summary>
    /// Sort companion for <c>Name</c>. The base field is analyzed for search, which tokenizes it, so ordering
    /// on it is meaningless for a value containing spaces. This carries the same value with no indexing
    /// declared, which keeps it a single un-tokenized term.
    /// </summary>
    [IgnoreProperty]
    public string NameSort { get; set; } = string.Empty;
}
