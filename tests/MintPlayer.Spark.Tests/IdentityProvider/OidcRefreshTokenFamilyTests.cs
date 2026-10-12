using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Refresh-token families are exact (MCP PRD decision log, "Refresh-token families exact"). A rotated refresh token
/// presented again, a refresh token spent twice at once, or an authorization code presented twice is a theft signal, and
/// the provider then revokes the family's active refresh token (RFC 9700 §4.14.2) and the tokens issued with it (RFC 6749
/// §4.1.2), decided by the family's document, loaded by id. Every case holds indexing back while the family grows, so the
/// sweep that also marks the tokens revoked (an index query, <c>Token.RevokeAuthorizationChainAsync</c>) cannot see the
/// tokens in question: only an exact decision refuses them.
/// </summary>
public class OidcRefreshTokenFamilyTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private static readonly string[] Scopes = ["openid", "offline_access"];

    private Task<OidcApplication> SeedRefreshableAppAsync(string name)
        => SeedApplicationAsync(ClientId(name), allowedScopes: Scopes, grantTypes: ["authorization_code", "refresh_token"]);

    private async Task<(HttpStatusCode Status, string? Refresh, string? Access)> TokenAsync(Dictionary<string, string> form)
    {
        using var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (response.StatusCode,
            body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            body.TryGetProperty("access_token", out var access) ? access.GetString() : null);
    }

    private Task<(HttpStatusCode Status, string? Refresh, string? Access)> RedeemAsync(OidcApplication app, string code)
        => TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        });

    private Task<(HttpStatusCode Status, string? Refresh, string? Access)> RefreshAsync(OidcApplication app, string refreshToken)
        => TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refreshToken,
        });

    /// <summary>Whether introspection, which asks the database rather than the signature, still vouches for the token.</summary>
    private async Task<bool> ActiveAsync(OidcApplication app, string token)
    {
        using var response = await Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["token"] = token,
        }));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("active").GetBoolean();
    }

    /// <summary>
    /// The user, the application and a first family, with indexing on: a reuse of that family runs the sweep, so its
    /// auto-index exists and has caught up before a case stops indexing. A query that had to create its index while
    /// indexing is stopped would test RavenDB, not the provider. Returns the user's id.
    /// </summary>
    private async Task<string> PrimeAsync(OidcApplication app, string email)
    {
        var user = await SeedUserAsync(email);
        var first = await RedeemAsync(app, await ObtainCodeAsync(app, email, Scopes));
        first.Status.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(app, first.Refresh!)).Status.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(app, first.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest);
        await Store.WaitForIndexingAsync();
        return user.Id!;
    }

    /// <summary>Runs <paramref name="body"/> while the database indexes nothing, so no index sees what it writes.</summary>
    private async Task WhileIndexingIsStoppedAsync(Func<Task> body)
    {
        await Store.WaitForIndexingAsync();
        await Store.Maintenance.SendAsync(new StopIndexingOperation());
        try
        {
            await body();
        }
        finally
        {
            await Store.Maintenance.SendAsync(new StartIndexingOperation());
        }
    }

    /// <summary>
    /// RFC 9700 §4.14.2: a rotated refresh token presented again revokes the active one, and the access token issued with
    /// it, though no index has seen either. Red before the family document: the successor refreshed (200), because the
    /// only teardown was the sweep's index query.
    /// </summary>
    [Fact]
    public async Task A_rotated_refresh_token_presented_again_revokes_the_active_one()
    {
        var app = await SeedRefreshableAppAsync("reuse");
        var email = UserEmail("reuse");
        await PrimeAsync(app, email);
        var issued = await RedeemAsync(app, await ObtainCodeAsync(app, email, Scopes));

        await WhileIndexingIsStoppedAsync(async () =>
        {
            var rotated = await RefreshAsync(app, issued.Refresh!);
            rotated.Status.Should().Be(HttpStatusCode.OK);
            (await ActiveAsync(app, rotated.Access!)).Should().BeTrue("the rotation's access token is live until the reuse");

            (await RefreshAsync(app, issued.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                "a rotated refresh token presented again is a theft signal");
            (await RefreshAsync(app, rotated.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                "the active refresh token of the family is revoked, though no index has seen it");
            (await ActiveAsync(app, rotated.Access!)).Should().BeFalse("and the access token issued with it");
        });
    }

    /// <summary>
    /// One refresh token presented four times at once: at most one rotation wins, and spending the token twice is a reuse,
    /// so whatever the winner was handed is revoked with the family. Red before the family document: the losers of the
    /// race were refused and nothing else happened, so the winner's successor refreshed (200).
    /// </summary>
    [Fact]
    public async Task A_refresh_token_spent_twice_at_once_lets_at_most_one_win_and_revokes_the_family()
    {
        var app = await SeedRefreshableAppAsync("race");
        var email = UserEmail("race");
        await PrimeAsync(app, email);
        var issued = await RedeemAsync(app, await ObtainCodeAsync(app, email, Scopes));

        await WhileIndexingIsStoppedAsync(async () =>
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RefreshAsync(app, issued.Refresh!)));

            var winners = attempts.Where(a => a.Status == HttpStatusCode.OK).ToList();
            winners.Count.Should().BeLessThanOrEqualTo(1, "one refresh token rotates once");
            foreach (var winner in winners)
            {
                (await RefreshAsync(app, winner.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                    "the second spend revoked the family, the winner's successor included");
                (await ActiveAsync(app, winner.Access!)).Should().BeFalse("and the winner's access token");
            }
            (await RefreshAsync(app, issued.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest);
        });
    }

    /// <summary>
    /// Withdrawing the grant ends the family through the grant link, though the sweep cannot see the active token: every
    /// redemption and introspection loads the grant (<c>OidcGrants.PermitsAsync</c>) beside the family. Green before the
    /// family document too (the link it must keep).
    /// </summary>
    [Fact]
    public async Task Withdrawing_the_grant_ends_the_family_though_no_index_has_seen_its_active_token()
    {
        var app = await SeedRefreshableAppAsync("withdraw");
        var email = UserEmail("withdraw");
        var userId = await PrimeAsync(app, email);
        var issued = await RedeemAsync(app, await ObtainCodeAsync(app, email, Scopes));

        await WhileIndexingIsStoppedAsync(async () =>
        {
            var rotated = await RefreshAsync(app, issued.Refresh!);
            rotated.Status.Should().Be(HttpStatusCode.OK);

            (await Factory.GetService<OidcGrantWithdrawal>().WithdrawAsync(userId, app.Id!, scopes: null, ipAddress: null, CancellationToken.None))
                .Should().BeTrue();

            (await RefreshAsync(app, rotated.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                "the user withdrew the grant the family was issued under");
            (await ActiveAsync(app, rotated.Access!)).Should().BeFalse("its access token goes with it");
        });
    }

    /// <summary>
    /// RFC 6749 §4.1.2: an authorization code presented twice revokes what it was redeemed for, the family it started. Red
    /// before the family document: the sweep could not see the tokens, and the refresh token still refreshed (200).
    /// </summary>
    [Fact]
    public async Task A_code_presented_twice_revokes_the_family_it_started()
    {
        var app = await SeedRefreshableAppAsync("replay");
        var email = UserEmail("replay");
        await PrimeAsync(app, email);
        var code = await ObtainCodeAsync(app, email, Scopes);

        await WhileIndexingIsStoppedAsync(async () =>
        {
            var issued = await RedeemAsync(app, code);
            issued.Status.Should().Be(HttpStatusCode.OK);

            (await RedeemAsync(app, code)).Status.Should().Be(HttpStatusCode.BadRequest, "a code is single-use");
            (await RefreshAsync(app, issued.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                "the replay revoked the family the code started");
            (await ActiveAsync(app, issued.Access!)).Should().BeFalse("its access token included");
        });
    }

    /// <summary>
    /// One code redeemed four times at once: at most one redemption wins, and the second spend revokes the family it
    /// started, as a later replay does. Red before the family document: the losers were refused and the winner's refresh
    /// token kept refreshing (200).
    /// </summary>
    [Fact]
    public async Task A_code_redeemed_twice_at_once_lets_at_most_one_win_and_revokes_the_family()
    {
        var app = await SeedRefreshableAppAsync("code-race");
        var email = UserEmail("code-race");
        await PrimeAsync(app, email);
        var code = await ObtainCodeAsync(app, email, Scopes);

        await WhileIndexingIsStoppedAsync(async () =>
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RedeemAsync(app, code)));

            var winners = attempts.Where(a => a.Status == HttpStatusCode.OK).ToList();
            winners.Count.Should().BeLessThanOrEqualTo(1, "a code is single-use");
            foreach (var winner in winners)
            {
                (await RefreshAsync(app, winner.Refresh!)).Status.Should().Be(HttpStatusCode.BadRequest,
                    "the second spend revoked the family the code started");
                (await ActiveAsync(app, winner.Access!)).Should().BeFalse("its access token included");
            }
        });
    }
}
