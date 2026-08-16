namespace MintPlayer.Spark.Tests.Collides;

/// <summary>
/// Deliberately shares its simple name with <c>MintPlayer.Spark.Tests.Model.IdemProbe</c>.
/// Model files are keyed by simple type name, so both resolve to the same <c>IdemProbe.json</c> —
/// which is how the stale-projection cleanup used to delete a file the same run had just written.
/// </summary>
public sealed class IdemProbe
{
    public string? Id { get; set; }
}
