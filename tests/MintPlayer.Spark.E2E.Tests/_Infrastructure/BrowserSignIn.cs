using Microsoft.Playwright;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Signs a browser page in to Fleet as a named user.
/// </summary>
/// <remarks>
/// Extracted so more than one test class can sign in, and as any user rather than only the admin.
/// The session cookie lands on the page's <see cref="IBrowserContext"/>, so every later navigation
/// in that context is authenticated — which is what makes "two users in two contexts" work.
/// </remarks>
public static class BrowserSignIn
{
    /// <summary>
    /// Signs in through the API rather than the login form.
    /// </summary>
    /// <remarks>
    /// The form is the right vehicle when the login UI itself is under test. For a test that is about
    /// something else, typing into it adds two locator waits and a router settle for no coverage —
    /// and each of those is a chance to fail for an unrelated reason.
    /// </remarks>
    public static async Task SignInAsync(IPage page, string fleetUrl, string email, string password)
    {
        var response = await page.APIRequest.PostAsync(
            $"{fleetUrl}/spark/auth/login?useCookies=true",
            new APIRequestContextOptions { DataObject = new { email, password } });

        if (!response.Ok)
            throw new InvalidOperationException(
                $"Signing in as {email} failed with {response.Status}: {await response.TextAsync()}");

        // The login POST's side effect is the cookie; confirm the round trip landed before navigating,
        // or the first page load races it and renders anonymous.
        if (!await WaitForAuthenticatedAsync(page, fleetUrl, TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException($"Signed in as {email} but /spark/auth/me never reported authenticated.");
    }

    /// <summary>Polls <c>/spark/auth/me</c> until it reports an authenticated session.</summary>
    /// <remarks>The timeout is a failure bound, not a measurement — never assert on how long it took.</remarks>
    public static async Task<bool> WaitForAuthenticatedAsync(IPage page, string fleetUrl, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var me = await page.APIRequest.GetAsync($"{fleetUrl}/spark/auth/me");
            if ((await me.TextAsync()).Contains("\"isAuthenticated\":true")) return true;
            await page.WaitForTimeoutAsync(250);
        }
        return false;
    }
}
