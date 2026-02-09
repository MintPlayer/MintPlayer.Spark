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
}
