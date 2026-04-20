using System.Collections.Generic;
using System.Linq;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// Shared helpers for security tests. The Spark middleware validates an antiforgery token
/// on every mutating request (POST/PUT/DELETE) against the <c>X-XSRF-TOKEN</c> header,
/// which must echo the current <c>XSRF-TOKEN</c> cookie. <see cref="IAPIRequestContext"/>
/// does not populate this header automatically, so tests that write must do so via
/// <see cref="SparkApi"/>.
/// </summary>
internal static class SecurityTestHelpers
{
    /// <summary>
    /// Returns an <see cref="IAPIRequestContext"/> wrapper that prepends the X-XSRF-TOKEN
    /// header on mutating calls. The caller must still explicitly log in (or not) before
    /// the mutating request is issued — this helper only handles CSRF, not auth.
    /// </summary>
    public static async Task<SparkApi> SparkApiForAsync(IPage page, FleetTestHost host)
    {
        // A primer GET causes the Spark middleware to mint an XSRF-TOKEN cookie.
        await page.APIRequest.GetAsync($"{host.FleetUrl}/spark/auth/me");
        var cookies = await page.Context.CookiesAsync(new[] { host.FleetUrl });
        var xsrf = cookies.FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value ?? "";
        return new SparkApi(page.APIRequest, host.FleetUrl, xsrf);
    }
}

/// <summary>
/// Thin wrapper that routes mutating calls through <see cref="IAPIRequestContext"/> with
/// the X-XSRF-TOKEN header set. Read-only GET/HEAD calls go through unchanged.
/// </summary>
internal sealed class SparkApi
{
    private readonly IAPIRequestContext _api;
    private readonly string _baseUrl;
    private readonly string _xsrfToken;

    public SparkApi(IAPIRequestContext api, string baseUrl, string xsrfToken)
    {
        _api = api;
        _baseUrl = baseUrl;
        _xsrfToken = xsrfToken;
    }

    private Dictionary<string, string> CsrfHeaders() =>
        new() { ["X-XSRF-TOKEN"] = _xsrfToken };

    public Task<IAPIResponse> GetAsync(string path) =>
        _api.GetAsync($"{_baseUrl}{path}");

    public Task<IAPIResponse> PostJsonAsync(string path, object body) =>
        _api.PostAsync($"{_baseUrl}{path}", new()
        {
            Headers = CsrfHeaders(),
            DataObject = body,
        });

    public Task<IAPIResponse> PutJsonAsync(string path, object body) =>
        _api.PutAsync($"{_baseUrl}{path}", new()
        {
            Headers = CsrfHeaders(),
            DataObject = body,
        });

    public Task<IAPIResponse> DeleteAsync(string path) =>
        _api.DeleteAsync($"{_baseUrl}{path}", new()
        {
            Headers = CsrfHeaders(),
        });

    /// <summary>
    /// Logs in as the given identity via the Identity API (cookie-based) and refreshes
    /// the XSRF token. Returns a new <see cref="SparkApi"/> bound to the post-login cookie.
    /// </summary>
    public static async Task<SparkApi> LoginAsync(IPage page, FleetTestHost host, string email, string password)
    {
        // Identity API endpoints are outside Spark's antiforgery surface — login works without XSRF.
        var loginResp = await page.APIRequest.PostAsync($"{host.FleetUrl}/spark/auth/login?useCookies=true", new()
        {
            DataObject = new { email, password },
        });
        if (loginResp.Status != 200)
            throw new InvalidOperationException(
                $"Login failed for {email} ({loginResp.Status}): {await loginResp.TextAsync()}");

        // After login, refresh the XSRF token bound to the authenticated principal.
        await page.APIRequest.GetAsync($"{host.FleetUrl}/spark/auth/me");
        var cookies = await page.Context.CookiesAsync(new[] { host.FleetUrl });
        var xsrf = cookies.FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value ?? "";
        return new SparkApi(page.APIRequest, host.FleetUrl, xsrf);
    }
}
