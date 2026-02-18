namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Role model for Spark authentication backed by RavenDB.
/// Uses deterministic document IDs based on the role name.
/// </summary>
public class SparkRole
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
    public List<SparkRoleClaim> Claims { get; set; } = [];
}
