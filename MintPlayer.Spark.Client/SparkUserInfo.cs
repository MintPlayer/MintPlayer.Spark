namespace MintPlayer.Spark.Client;

/// <summary>
/// Shape of <c>GET /spark/auth/me</c>. <see cref="IsAuthenticated"/> is the only non-null
/// field when the caller isn't signed in; the others populate once the session cookie is
/// attached.
/// </summary>
public sealed class SparkUserInfo
{
    public bool IsAuthenticated { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string[] Roles { get; init; } = [];
}
