namespace MintPlayer.Spark.Cli.Services;

/// <summary>
/// The two reachability checks <c>spark new</c> makes before it writes anything: the local RavenDB the template points
/// at, and the OIDC authority's discovery document. Either only warns; neither is needed to generate the app.
/// </summary>
public sealed class Probes
{
    /// <summary>The server the template's appsettings.json names.</summary>
    public const string RavenDbUrl = "http://localhost:8080";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Asks 127.0.0.1 and localhost at once, and the first that answers wins. <c>localhost</c> resolves to ::1 first,
    /// and where something swallows connections on ::1:8080 (measured on the owner's machine, 2026-11-17: 2 s timeout
    /// via localhost, 200 in 13 ms via 127.0.0.1) HttpClient does not fall back to IPv4 the way curl does. No proxy:
    /// Windows' proxy auto-detection can stall a first request for seconds.
    /// </summary>
    public async Task<bool> RavenDbReachableAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<bool>>
        {
            GetAsync(new Uri("http://127.0.0.1:8080/build/version"), useProxy: false, linked.Token),
            GetAsync(new Uri($"{RavenDbUrl}/build/version"), useProxy: false, linked.Token),
        };
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (await done)
            {
                await linked.CancelAsync();
                return true;
            }
        }
        return false;
    }

    public Task<bool> OidcDiscoveryReachableAsync(string authority, CancellationToken cancellationToken)
        => Uri.TryCreate($"{authority.TrimEnd('/')}/.well-known/openid-configuration", UriKind.Absolute, out var uri)
            ? GetAsync(uri, useProxy: true, cancellationToken)
            : Task.FromResult(false);

    private static async Task<bool> GetAsync(Uri uri, bool useProxy, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = useProxy };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(uri, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
