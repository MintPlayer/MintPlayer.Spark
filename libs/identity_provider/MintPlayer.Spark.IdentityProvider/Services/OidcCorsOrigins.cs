using Microsoft.Extensions.Logging;

using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The set of browser origins the identity provider's protocol endpoints answer cross-origin — the
/// union of <see cref="OidcApplication.AllowedCorsOrigins"/> across every <b>enabled</b>
/// application.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>A union, and it has to be.</b> A CORS preflight is an anonymous <c>OPTIONS</c> with no
/// body, no cookies and no <c>client_id</c>, so the policy cannot ask "which application is this?"
/// — the only question it can answer is "is this origin registered by anybody?". Anyone reaching
/// for per-client narrowing will find the same wall.
/// </para>
/// <para>
/// ⚠️ <b>Cached, because <c>SetIsOriginAllowed</c> is synchronous.</b> It runs on every preflight
/// and every actual cross-origin request, so a database round trip per call is not an option
/// whatever seam it hangs off. The snapshot is swapped by reference, loaded once at startup,
/// invalidated when an application is written, and re-read on a TTL for edits made outside the app
/// (Raven Studio, another node, a replicated write).
/// </para>
/// <para>
/// ⚠️ <b>Fails closed.</b> Before the first load, nothing is allowed. A browser does not retry a
/// failed preflight, so "allow while loading" would be a real hole that closes itself just late
/// enough to be invisible in testing.
/// </para>
/// </remarks>
public sealed class OidcCorsOrigins
{
    /// <summary>How long a snapshot is trusted before it is refreshed in the background.</summary>
    /// <remarks>
    /// Only a backstop for writes this process did not make — an in-app save invalidates
    /// immediately via <see cref="Invalidate"/>.
    /// </remarks>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private IDocumentStore? _store;
    private ILogger? _logger;

    private volatile HashSet<string>? _origins;
    private long _loadedAtTicks;
    private int _refreshing;

    /// <summary>
    /// Hands over the store and logger once the application has been built.
    /// </summary>
    /// <remarks>
    /// ⚠️ Two-phase on purpose. The CORS policy is declared during service registration, where
    /// there is no <see cref="IDocumentStore"/> to resolve yet, and a policy's
    /// <c>SetIsOriginAllowed</c> predicate has no service provider of its own — so the instance has
    /// to exist before the thing it reads does. Until this is called, nothing is allowed.
    /// </remarks>
    public void Initialize(IDocumentStore store, ILogger? logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="origin"/> — an <c>Origin</c> header value, scheme + host + optional
    /// port and nothing else — is registered. Synchronous, allocation-free on the hit path, and
    /// never blocks on I/O.
    /// </summary>
    public bool IsAllowed(string origin)
    {
        var snapshot = _origins;

        // Fail closed until the first load lands.
        if (snapshot is null) return false;

        if (IsStale) _ = RefreshInBackground();

        return snapshot.Contains(origin);
    }

    private bool IsStale
        => DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) > RefreshInterval.Ticks;

    /// <summary>
    /// Loads the snapshot. Called once at startup so the first preflight never races an empty set.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null) return;

        using var session = _store.OpenAsyncSession();

        // ⚠️ A collection query, materialising whole documents — deliberately NOT
        // OidcApplications_ByClientId + ProjectInto. That index maps ClientId and Enabled only, and
        // a projection off it would hand back applications whose AllowedCorsOrigins is empty, with
        // no error: CORS would simply refuse every origin. Projecting needs the index to store the
        // field being read, and this one has no reason to.
        var applications = await session
            .Query<OidcApplication>()
            .Where(a => a.Enabled)
            .ToListAsync(cancellationToken);

        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var application in applications)
            foreach (var raw in application.AllowedCorsOrigins)
            {
                var normalized = Normalize(raw);
                if (normalized is null)
                {
                    // Warn rather than drop it into the set, where it could never match and the
                    // operator would see a silently ineffective configuration entry.
                    _logger?.LogWarning(
                        "OIDC application {ClientId} declares an AllowedCorsOrigins entry that is not a bare origin " +
                        "and will never match a browser's Origin header: {Origin}",
                        application.ClientId, raw);
                    continue;
                }
                origins.Add(normalized);
            }

        _origins = origins;
        Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);

        if (origins.Count == 0)
            _logger?.LogWarning(
                "The identity provider's dynamic CORS is enabled, but no enabled OIDC application declares an " +
                "AllowedCorsOrigins entry — so no origin is allowed and every cross-origin call to the protocol " +
                "endpoints will fail with a missing header rather than an error.");
    }

    /// <summary>Drops the snapshot's freshness so the next call reloads. Call after writing an application.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _loadedAtTicks, 0);

    private async Task RefreshInBackground()
    {
        // One refresh at a time; the others keep serving the current snapshot.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // A failed refresh must not empty the set — the previous snapshot stays in place, and
            // the staleness check will try again.
            _logger?.LogWarning(ex, "Refreshing the OIDC CORS origin snapshot failed; serving the previous one.");
            Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Reduces a configured entry to the exact shape a browser sends, or returns null when it is not
    /// an origin at all.
    /// </summary>
    /// <remarks>
    /// ⚠️ A trailing slash is the trap worth knowing: an <c>Origin</c> header is never
    /// <c>https://app.example.com/</c>, so a configured value with one is a rule that can never
    /// match and gives no sign of it.
    /// </remarks>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.PathAndQuery is not ("" or "/")) return null;
        if (!string.IsNullOrEmpty(uri.Fragment)) return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
