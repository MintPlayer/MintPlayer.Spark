using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.CustomActions;
using MintPlayer.Spark.IdentityProvider.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// I11 operations (D9): the <c>INC:Tokens</c> usage counter, the disable cascade (G26) and the
/// <c>RevokeGrant</c> action.
/// <para>
/// The cascades are driven through the interceptors' <c>OnAfterSaveAsync</c> with a real session and
/// a <c>Before</c> snapshot, which is how the persistent-object pipeline calls them; the tokens and grants
/// they act on come from the real authorize → consent → token flow, so they carry the authorization ids
/// and scopes issuance actually stamps. Every scope and client id is unique to its case, so the cases
/// share one host even though a scope cascade patches the whole collection.
/// </para>
/// </summary>
public class OidcOperationsTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";

    private sealed record Issued(OidcApplication App, SparkUser User, string AccessToken, string? RefreshToken);

    /// <summary>Seeds an application and a user and runs the code flow for <paramref name="scopes"/>.</summary>
    private async Task<Issued> IssueAsync(string name, string[] scopes, SparkUser? user = null)
    {
        var app = await SeedApplicationAsync(ClientId(name),
            allowedScopes: [.. scopes.Union(["openid", "offline_access"])],
            grantTypes: ["authorization_code", "refresh_token"]);
        user ??= await SeedUserAsync(UserEmail("alice"));

        var code = await ObtainCodeAsync(app, user.Email!, scopes);
        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return new Issued(app, user,
            body.GetProperty("access_token").GetString()!,
            body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null);
    }

    private static SaveContext Saving<T>(T entity, T before, IAsyncDocumentSession session) where T : class => new()
    {
        EntityType = typeof(T),
        Operation = PersistentObjectOperation.Save,
        PersistentObject = new PersistentObject { Name = typeof(T).Name, ObjectTypeId = Guid.NewGuid() },
        Entity = entity,
        Before = before,
        Session = session,
    };

    private static OidcApplicationInterceptors ApplicationInterceptors() => new(
        corsOrigins: new OidcCorsOrigins(),
        accessControl: Substitute.For<MintPlayer.Spark.Abstractions.Authorization.IAccessControl>(),
        options: new SparkIdentityProviderOptions(),
        audit: new OidcAudit(new SparkIdentityProviderOptions()));

    private async Task<OidcGrant?> GrantAsync(Issued issued)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(issued.User.Id!, issued.App.Id!));
    }

    private async Task<List<OidcToken>> TokensOfAsync(OidcApplication app)
    {
        using var session = Store.OpenAsyncSession();
        return [.. (await CaseTokensAsync(session)).Where(t =>
            t.ApplicationId == app.Id && t.Type is OidcTokenTypes.AccessToken or OidcTokenTypes.RefreshToken)];
    }

    private async Task<double> TokensIssuedAsync(OidcApplication app)
    {
        using var session = Store.OpenAsyncSession();
        var entries = await session.IncrementalTimeSeriesFor(app.Id!, OidcExpiry.TokensTimeSeries).GetAsync();
        return entries?.Sum(e => e.Value) ?? 0;
    }

    // ---------- INC:Tokens ----------

    [Fact]
    public async Task Every_access_token_issued_counts_one_on_the_applications_INC_Tokens_series()
    {
        var issued = await IssueAsync("counted", ["openid", "offline_access"]);

        (await TokensIssuedAsync(issued.App)).Should().Be(1, "the code grant issued one access token");

        var refresh = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = issued.App.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = issued.RefreshToken!,
        }));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);

        (await TokensIssuedAsync(issued.App)).Should().Be(2, "the refresh grant issued a second one");
    }

    [Fact]
    public async Task A_refused_token_request_counts_nothing()
    {
        var app = await SeedApplicationAsync(ClientId("uncounted"));

        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = OidcTokenReference.GenerateValue(),
            ["redirect_uri"] = app.RedirectUris[0],
        }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await TokensIssuedAsync(app)).Should().Be(0);
    }

    // ---------- disable cascade: application ----------

    [Fact]
    public async Task Disabling_an_application_revokes_its_valid_tokens_and_grants_and_nobody_elses()
    {
        var disabled = await IssueAsync("disabled", ["openid", "offline_access"]);
        var bystander = await IssueAsync("bystander", ["openid", "offline_access"], disabled.User);

        using (var session = Store.OpenAsyncSession())
        {
            var app = await session.LoadAsync<OidcApplication>(disabled.App.Id);
            var before = new OidcApplication { Id = app.Id, ClientId = app.ClientId, Enabled = true };
            app.Enabled = false;
            await session.SaveChangesAsync();

            await ApplicationInterceptors().OnAfterSaveAsync(app, Saving(app, before, session));
        }

        var tokens = await TokensOfAsync(disabled.App);
        tokens.Should().NotBeEmpty();
        tokens.Should().OnlyContain(t => t.Status == "revoked", "a disabled application's tokens die with it");
        (await GrantAsync(disabled))!.Status.Should().Be("revoked", "its users consent again if it comes back");

        (await TokensOfAsync(bystander.App)).Should().OnlyContain(t => t.Status == "valid");
        (await GrantAsync(bystander))!.Status.Should().Be("valid");
    }

    [Fact]
    public async Task Saving_an_application_that_was_already_disabled_patches_nothing()
    {
        var issued = await IssueAsync("stilldisabled", ["openid"]);

        using (var session = Store.OpenAsyncSession())
        {
            var app = await session.LoadAsync<OidcApplication>(issued.App.Id);
            // Only the Enabled → disabled transition cascades; Before says it was off already.
            var before = new OidcApplication { Id = app.Id, ClientId = app.ClientId, Enabled = false };
            app.Enabled = false;
            await session.SaveChangesAsync();

            await ApplicationInterceptors().OnAfterSaveAsync(app, Saving(app, before, session));
        }

        (await TokensOfAsync(issued.App)).Should().OnlyContain(t => t.Status == "valid");
        (await GrantAsync(issued))!.Status.Should().Be("valid");
    }

    // ---------- disable cascade: API scope ----------

    [Fact]
    public async Task Disabling_an_api_scope_revokes_the_tokens_carrying_it_but_keeps_the_grants()
    {
        var api = ClientId("ledger");
        var scope = api + ".read";
        var carrying = await IssueAsync("carrying", ["openid", scope]);
        var without = await IssueAsync("without", ["openid"], carrying.User);

        carrying.App.ScopeNames().Should().Contain(scope);
        (await TokensOfAsync(carrying.App)).Should().Contain(t => t.Scopes.Contains(scope),
            "the issued access token carries the API scope");

        using (var session = Store.OpenAsyncSession())
        {
            var resource = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(api));
            var before = new OidcResource
            {
                Id = resource.Id, Kind = resource.Kind, Name = resource.Name, Enabled = true,
                Scopes = [.. resource.Scopes.Select(s => new OidcApiScope { Name = s.Name, Enabled = true })],
            };
            resource.Scopes.Single(s => s.Name == scope).Enabled = false;
            await session.SaveChangesAsync();

            await new OidcResourceInterceptors(audit: new OidcAudit(new SparkIdentityProviderOptions()))
                .OnAfterSaveAsync(resource, Saving(resource, before, session));
        }

        (await TokensOfAsync(carrying.App)).Where(t => t.Scopes.Contains(scope))
            .Should().OnlyContain(t => t.Status == "revoked", "a token carrying a disabled scope is taken back");
        (await GrantAsync(carrying))!.Status.Should().Be("valid",
            "grants stay: re-enabling the scope honours the consent already given");

        (await TokensOfAsync(without.App)).Should().OnlyContain(t => t.Status == "valid",
            "tokens without the scope are untouched");
    }

    // ---------- RevokeGrant ----------

    private RevokeGrantAction RevokeGrant(IAsyncDocumentSession session, bool administrator) => new(
        manager: Substitute.For<IManager>(),
        session: session,
        access: new OidcPortalAccess(
            httpContextAccessor: new HttpContextAccessor(),
            accessControl: administrator ? SparkTestAccessControl.AllowAll() : SparkTestAccessControl.DenyAll()),
        withdrawal: new OidcGrantWithdrawal(Store, new OidcAudit(new SparkIdentityProviderOptions())));

    /// <summary>The withdrawal's token sweep reads an index; let it catch up so the case is about the action.</summary>
    private async Task WarmGrantSweepIndexAsync(string grantId)
    {
        using (var session = Store.OpenAsyncSession())
            _ = await session.Query<OidcToken>().Where(t => t.AuthorizationId == grantId && t.Status == "valid").ToListAsync();
        await Store.WaitForIndexingAsync();
    }

    [Fact]
    public async Task RevokeGrant_revokes_the_grant_and_the_tokens_issued_under_it()
    {
        var issued = await IssueAsync("revoked", ["openid", "offline_access"]);
        var grantId = OidcGrantReference.DocumentId(issued.User.Id!, issued.App.Id!);
        await WarmGrantSweepIndexAsync(grantId);

        using (var session = Store.OpenAsyncSession())
        {
            await RevokeGrant(session, administrator: true).ExecuteAsync(new CustomActionArgs
            {
                SelectedItems = [new QueryResultItem { Id = grantId, Values = [] }],
            });
        }

        (await GrantAsync(issued))!.Status.Should().Be("revoked");
        (await TokensOfAsync(issued.App)).Should().OnlyContain(t => t.Status == "revoked");

        var refresh = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = issued.App.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = issued.RefreshToken!,
        }));
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest, "nothing issued under a revoked grant mints anything");
    }

    [Fact]
    public async Task RevokeGrant_does_nothing_for_a_caller_who_is_not_an_administrator()
    {
        var issued = await IssueAsync("kept", ["openid", "offline_access"]);
        var grantId = OidcGrantReference.DocumentId(issued.User.Id!, issued.App.Id!);

        using (var session = Store.OpenAsyncSession())
        {
            await RevokeGrant(session, administrator: false).ExecuteAsync(new CustomActionArgs
            {
                SelectedItems = [new QueryResultItem { Id = grantId, Values = [] }],
            });
        }

        (await GrantAsync(issued))!.Status.Should().Be("valid");
        (await TokensOfAsync(issued.App)).Should().OnlyContain(t => t.Status == "valid");
    }
}
