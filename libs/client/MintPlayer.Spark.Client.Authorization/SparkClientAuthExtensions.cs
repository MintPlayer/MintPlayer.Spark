using System.Net.Http.Json;
using System.Text.Json;

namespace MintPlayer.Spark.Client.Authorization;

/// <summary>
/// Extension methods that add Spark authentication endpoints to
/// <see cref="SparkClient"/> — <c>/spark/auth/login</c>, <c>/register</c>, <c>/logout</c>,
/// and <c>/me</c>. These are packaged separately because the server-side Authentication
/// support is itself a separate nuget (<c>MintPlayer.Spark.Authorization</c>); apps that
/// don't need auth don't pay the cost here either.
///
/// Implementation sits on <see cref="SparkClient.SendAsync"/> and
/// <see cref="SparkClient.InvalidateAntiforgery"/> so the package doesn't need access to
/// the client's internal cookie/CSRF state.
/// </summary>
public static class SparkClientAuthExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Signs in via <c>POST /spark/auth/login?useCookies=true</c>. The identity API endpoint
    /// is outside Spark's antiforgery surface, so no CSRF header is sent. After login, the
    /// client's pre-auth XSRF token is invalidated and re-primed via
    /// <see cref="GetCurrentUserAsync"/> — the token is bound to the authenticated principal
    /// and the anonymous one is no longer valid for mutating calls.
    /// </summary>
    public static async Task LoginAsync(
        this SparkClient client,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var content = JsonContent.Create(new { email, password }, options: JsonOptions);
        using var response = await client.SendAsync(
            HttpMethod.Post,
            "/spark/auth/login?useCookies=true",
            content,
            requiresAntiforgery: false,
            cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);

        // Pre-auth XSRF token is no longer valid — drop it and re-prime on the next
        // mutating call. We eagerly hit /me here to pull a fresh principal-bound token
        // via Set-Cookie, matching the server's behaviour of rotating the XSRF cookie on
        // identity change.
        client.InvalidateAntiforgery();
        await client.GetCurrentUserAsync(cancellationToken);
    }

    /// <summary>
    /// Registers a new user via <c>POST /spark/auth/register</c>. Outside Spark's antiforgery
    /// surface. Does not automatically sign the user in — call <see cref="LoginAsync"/> if
    /// that's the intent.
    /// </summary>
    public static async Task RegisterAsync(
        this SparkClient client,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var content = JsonContent.Create(new { email, password }, options: JsonOptions);
        using var response = await client.SendAsync(
            HttpMethod.Post,
            "/spark/auth/register",
            content,
            requiresAntiforgery: false,
            cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Signs out via <c>POST /spark/auth/logout</c>. The logout endpoint is inside Spark's
    /// antiforgery surface, so the client attaches the X-XSRF-TOKEN header. After logout
    /// the XSRF token is invalidated so the next mutating call re-primes against the
    /// anonymous principal.
    /// </summary>
    public static async Task LogoutAsync(
        this SparkClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.SendAsync(
            HttpMethod.Post,
            "/spark/auth/logout",
            content: null,
            requiresAntiforgery: true,
            cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);

        client.InvalidateAntiforgery();
    }

    /// <summary>
    /// Returns <c>GET /spark/auth/me</c> — the lightweight "who am I" payload. Also serves
    /// as a warmup that refreshes the XSRF token bound to whatever principal the current
    /// cookies represent (authenticated or anonymous) via the Set-Cookie response header.
    /// </summary>
    public static async Task<SparkUserInfo> GetCurrentUserAsync(
        this SparkClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.SendAsync(
            HttpMethod.Get,
            "/spark/auth/me",
            cancellationToken: cancellationToken);
        await SparkClientException.ThrowIfNotSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SparkUserInfo>(JsonOptions, cancellationToken)
            ?? throw new SparkClientException(response.StatusCode, responseBody: null, "Empty /me response.");
    }
}
