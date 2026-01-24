using DemoApp.Indexes;
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
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
