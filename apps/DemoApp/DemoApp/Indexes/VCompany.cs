using MintPlayer.Spark.Abstractions;

namespace DemoApp.Indexes;

[FromIndex(typeof(Companies_Overview))]
public partial class VCompany
{
    public string? Id { get; set; }
    [Search]
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public int? EmployeeCount { get; set; }
}
