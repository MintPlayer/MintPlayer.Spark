using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions;

namespace DemoApp.Indexes;

/// <summary>
/// View model for Person used by the People_Overview RavenDB index.
/// Contains computed/projected properties optimized for list views.
/// </summary>
[FromIndex(typeof(People_Overview))]
public partial class VPerson
{
    public string? Id { get; set; }
    [Search]
    public string FullName { get; set; } = string.Empty;
    [Search]
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    [Reference(typeof(Company))]
    public string? Company { get; set; }

}
