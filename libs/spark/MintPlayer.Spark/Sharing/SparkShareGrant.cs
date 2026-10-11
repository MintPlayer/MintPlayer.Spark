namespace MintPlayer.Spark.Sharing;

/// <summary>
/// The rights of an anonymous share read, for the one DI scope it runs in (X6). While active, the permission
/// service answers from this list only: <c>Read/{Type}</c> of the shared type and <c>Query/{EntityType}</c> of
/// its declared child queries, nothing else, whatever <c>security.json</c> grants anyone. The read runs under a
/// system principal, so row policies that bypass the system context (organizations, an Actions class's
/// <c>GetRowFilterAsync</c>) are skipped and the ones that do not (expiration, X1) still hide what they hide.
/// Which object is read is fixed by the share service before anything runs: the token's object, and queries with
/// it as their parent.
/// </summary>
internal sealed class SparkShareGrant
{
    private HashSet<string>? allowed;

    public bool IsActive => allowed is not null;

    public void Activate(IEnumerable<string> resources) => allowed = new HashSet<string>(resources, StringComparer.OrdinalIgnoreCase);

    public bool Allows(string resource) => allowed?.Contains(resource) == true;
}
