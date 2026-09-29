using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Moderation.Services;
using MintPlayer.Spark.Tests.Authorization.Extensions;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 M12 spike S-MOD-C: how long a suspended (locked-out, security stamp refreshed) user's cookie
/// and bearer token keep working. Measured on a controllable clock, never by waiting.
/// </summary>
public class ModerationSuspensionSpikeTests(ITestOutputHelper output) : MintPlayer.Spark.Testing.SparkTestDriver
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task S_MOD_C_a_cookie_survives_until_the_next_stamp_validation_and_a_bearer_token_until_it_expires()
    {
        var clock = new MoClock { Now = DateTimeOffset.UtcNow };
        await using var host = await AccountTestHost.StartAsync(Store,
            services: s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
                s.Configure<SecurityStampValidatorOptions>(o =>
                {
                    o.ValidationInterval = Interval;
                    o.TimeProvider = clock;
                });
                s.ConfigureAll<CookieAuthenticationOptions>(o => o.TimeProvider = clock);
                s.ConfigureAll<BearerTokenOptions>(o =>
                {
                    o.TimeProvider = clock;
                    o.BearerTokenExpiration = TimeSpan.FromHours(1);
                });
            },
            endpoints: e => e.MapGet("/whoami", async (HttpContext c) =>
            {
                var cookie = await c.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                var bearer = await c.AuthenticateAsync(IdentityConstants.BearerScheme);
                return Results.Json(new { cookie = cookie.Succeeded, bearer = bearer.Succeeded });
            }));
        var user = await host.CreateUserAsync("suspect", "suspect@example.com");
        using var client = host.Client();

        var cookie = await host.CookieSignInAsync(client, "suspect@example.com");
        var login = await client.PostAsJsonAsync("/spark/auth/login", new { email = "suspect@example.com", password = AccountTestHost.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        var access = tokens.GetProperty("accessToken").GetString()!;
        var refresh = tokens.GetProperty("refreshToken").GetString()!;

        async Task<(bool Cookie, bool Bearer)> WhoAmIAsync()
        {
            var byCookie = JsonDocument.Parse(await (await AccountTestHost.SendAsync(client, HttpMethod.Get, "/whoami", cookie: cookie)).Content.ReadAsStringAsync()).RootElement;
            var byBearer = JsonDocument.Parse(await (await AccountTestHost.SendAsync(client, HttpMethod.Get, "/whoami", bearer: access)).Content.ReadAsStringAsync()).RootElement;
            return (byCookie.GetProperty("cookie").GetBoolean(), byBearer.GetProperty("bearer").GetBoolean());
        }

        (await WhoAmIAsync()).Should().Be((true, true));

        // Suspend: lockout + security-stamp refresh, exactly as Moderation does it.
        await host.WithScopeAsync(async sp =>
        {
            var accounts = ActivatorUtilities.CreateInstance<IdentityModerationAccounts<SparkUser>>(sp);
            await accounts.LockOutAsync(user.Id!, null);
            return true;
        });

        var rows = new List<string>();
        foreach (var minutes in new[] { 0, 29, 31, 59, 61 })
        {
            clock.Now = clock.Now.AddMinutes(minutes - (rows.Count == 0 ? 0 : int.Parse(rows[^1].Split(' ')[0])));
            var (c, b) = await WhoAmIAsync();
            rows.Add($"{minutes} min: cookie={c} bearer={b}");
        }
        var refreshed = await client.PostAsJsonAsync("/spark/auth/refresh", new { refreshToken = refresh });
        var relogin = await client.PostAsJsonAsync("/spark/auth/login", new { email = "suspect@example.com", password = AccountTestHost.Password });
        foreach (var row in rows)
            output.WriteLine("S-MOD-C " + row);
        output.WriteLine($"S-MOD-C refresh after suspension: {(int)refreshed.StatusCode}; new password sign-in: {(int)relogin.StatusCode}");

        rows.Should().Equal(
            "0 min: cookie=True bearer=True",
            "29 min: cookie=True bearer=True",
            "31 min: cookie=False bearer=True",
            "59 min: cookie=False bearer=True",
            "61 min: cookie=False bearer=False");
        refreshed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the refresh validates the stamp");
        relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "locked out");
    }
}
