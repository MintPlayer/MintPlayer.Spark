using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Tests.IdentityProvider;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 M5 (<c>docs/apps_webhooks_PRD.md</c> D16, D17, D30): installing an App grants it what it asks for; events reach
/// it only within the granted permissions; its installation token carries those permissions and dies with the
/// installation.
/// </summary>
public class AppInstallationTests : AppsTestHost
{
    private const string Issuer = "https://idp.example.com";

    protected override void ConfigureAppsBuilder(AppsBuilder apps) => apps.EnableApps().MapPermission("orders:read", "OrderReaders");

    protected override void ConfigureSpark(ISparkBuilder spark) => spark.AddIdentityProvider(o =>
    {
        o.Issuer = Issuer;
        o.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
    });

    private async Task<string> CreateAppAsync(string userId, string slug, string[] permissions, string[] events, bool isPublic = true)
    {
        var (status, body) = await SendAsync("/spark/apps/apps", new
        {
            ownerKind = WebhookOwner.UserKind,
            ownerKey = userId,
            slug,
            name = slug,
            webhookUrl = ReceiverUrl,
            permissions,
            events,
            @public = isPublic,
            setupUrl = "https://app.example.com/setup",
        }, userId);
        status.Should().Be(HttpStatusCode.OK, "the developer registers the App: " + body);
        return body.GetProperty("app").GetProperty("webhookEndpointId").GetString()!;
    }

    private async Task<string> InstallAsync(string slug, string userId)
    {
        var (status, body) = await SendAsync("/spark/apps/installations", new { slug, targetKind = WebhookOwner.UserKind, targetKey = userId }, userId);
        status.Should().Be(HttpStatusCode.OK, "the user installs a public App on their own account: " + body);
        return body.GetProperty("installation").GetProperty("id").GetString()!;
    }

    private List<JsonElement> ReceivedOfType(string type)
        => Receiver.Received.Select(r => r.Json).Where(j => j.GetProperty("type").GetString() == type).ToList();

    [Fact]
    public async Task An_event_reaches_an_installed_App_only_within_the_permissions_its_installer_granted()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var carol = await SeedUserAsync("carol");
        var hook = await CreateAppAsync(ann.Id!, "billing", [], ["invoice.paid"]);
        await UntilSettledAsync(hook, 1);

        // The consent screen offers bob his own account; installing tells the App, and sends bob on to its setup URL.
        var (_, prompt) = await GetAsync("/spark/apps/installations/new?slug=billing", bob.Id);
        prompt.GetProperty("targets")[0].GetProperty("key").GetString().Should().Be(bob.Id);
        var (_, installed) = await SendAsync("/spark/apps/installations", new { slug = "billing", targetKind = WebhookOwner.UserKind, targetKey = bob.Id }, bob.Id);
        var installationId = installed.GetProperty("installation").GetProperty("id").GetString()!;
        installed.GetProperty("redirectUrl").GetString().Should().Be($"https://app.example.com/setup?installation_id={installationId}&setup_action=install");
        await UntilSettledAsync(hook, 2);
        ReceivedOfType("installation.created").Should().ContainSingle("the App learns where it was installed")
            .Which.GetProperty("data").GetProperty("installation").GetProperty("id").GetString().Should().Be(installationId);

        // The App now asks for orders:read and order.shipped; bob has not accepted.
        (await SendAsync("/spark/apps/apps/update", new { slug = "billing", permissions = new[] { "orders:read" }, events = new[] { "invoice.paid", "order.shipped" } }, ann.Id))
            .Status.Should().Be(HttpStatusCode.OK);
        var (_, pending) = await GetAsync($"/spark/apps/installations?targetKind={WebhookOwner.UserKind}&targetKey={bob.Id}", bob.Id);
        pending[0].GetProperty("pendingPermissions")[0].GetString().Should().Be("orders:read", "bob sees what waits for his consent");

        (await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(bob.Id!))).Should().NotBeNull();
        (await PublishAsync(new InvoicePaid("i-1", 10m, DateTimeOffset.UtcNow), WebhookOwner.User(bob.Id!))).Should().NotBeNull();
        (await PublishAsync(new InvoicePaid("i-2", 10m, DateTimeOffset.UtcNow), WebhookOwner.User(carol.Id!))).Should().BeNull("carol has no destination at all");
        await UntilSettledAsync(hook, 3);
        await Task.Delay(500);
        ReceivedOfType("order.shipped").Should().BeEmpty("bob never granted orders:read, so the App never sees his orders");
        var invoice = ReceivedOfType("invoice.paid").Should().ContainSingle("only bob's event reaches the App").Which;
        invoice.GetProperty("installation").GetProperty("id").GetString().Should().Be(installationId, "the payload names the installation to ask a token for");

        (await SendAsync("/spark/apps/installations/accept", new { id = installationId }, bob.Id)).Status.Should().Be(HttpStatusCode.OK);
        await PublishAsync(new OrderShipped("o-2", 1, null), WebhookOwner.User(bob.Id!));
        await UntilSettledAsync(hook, 5);
        ReceivedOfType("installation.new_permissions_accepted").Should().ContainSingle();
        ReceivedOfType("order.shipped").Should().ContainSingle("after bob accepts, his orders reach the App")
            .Which.GetProperty("data").GetProperty("orderId").GetString().Should().Be("o-2");
    }

    [Fact]
    public async Task A_private_App_installs_only_on_its_owner_and_a_second_install_is_a_conflict()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "in-house", ["orders:read"], ["order.shipped"], isPublic: false);

        (await GetAsync("/spark/apps/installations/new?slug=in-house", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound, "a private App is invisible to others");
        (await SendAsync("/spark/apps/installations", new { slug = "in-house", targetKind = WebhookOwner.UserKind, targetKey = bob.Id }, bob.Id))
            .Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/apps/installations", new { slug = "in-house", targetKind = WebhookOwner.UserKind, targetKey = ann.Id }, bob.Id))
            .Status.Should().Be(HttpStatusCode.NotFound, "bob cannot install anything on ann's account");

        await InstallAsync("in-house", ann.Id!);
        (await SendAsync("/spark/apps/installations", new { slug = "in-house", targetKind = WebhookOwner.UserKind, targetKey = ann.Id }, ann.Id))
            .Status.Should().Be(HttpStatusCode.Conflict, "an App is installed once per target");
        var (_, list) = await GetAsync("/spark/apps/apps/installations?slug=in-house", ann.Id);
        list.GetArrayLength().Should().Be(1, "the App's owner lists its installations");
    }

    [Fact]
    public async Task The_installation_token_carries_the_granted_permissions_and_dies_with_suspension_and_uninstall()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "shipper", ["orders:write"], ["order.shipped"]);
        await CreateAppAsync(ann.Id!, "other", [], ["invoice.paid"]);
        var (_, key) = await SendAsync("/spark/apps/apps/keys/generate", new { slug = "shipper" }, ann.Id);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.GetProperty("privateKeyPem").GetString()!);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = key.GetProperty("key").GetProperty("keyId").GetString() };
        string clientId;
        using (var session = Store.OpenAsyncSession())
            clientId = (await session.LoadAsync<SparkApp>(SparkApp.IdFor("shipper").Value))!.ClientId;

        var installationId = await InstallAsync("shipper", bob.Id!);
        var otherInstallation = await InstallAsync("other", bob.Id!);

        var (status, token) = await TokenAsync(installationId);
        status.Should().Be(HttpStatusCode.OK, "the App holds a key and the installation is live: " + token);
        token.GetProperty("scope").GetString().Should().Be("orders:write", "the token carries what bob granted");
        var jwt = new JsonWebToken(token.GetProperty("access_token").GetString());
        jwt.GetClaim("installation_id").Value.Should().Be(installationId);
        jwt.GetClaim("installation_target").Value.Should().Be(bob.Id);
        jwt.TryGetClaim("sub", out _).Should().BeFalse("an installation token speaks for no user");
        (await IntrospectAsync(jwt.EncodedToken)).Should().BeTrue();

        (await TokenAsync(installationId, scope: "orders:read")).Body.GetProperty("scope").GetString().Should().Be("orders:read", "write covers read, and a token may ask for less");
        (await TokenAsync(installationId, scope: "invoices:read")).Status.Should().Be(HttpStatusCode.BadRequest, "nothing beyond the grant");
        (await TokenAsync(otherInstallation)).Body.GetProperty("error").GetString().Should().Be("invalid_grant", "another App's installation is not this App's to use");
        (await TokenAsync("in_000000000000000000000000")).Body.GetProperty("error").GetString().Should().Be("invalid_grant");

        (await SendAsync("/spark/apps/installations/suspend", new { id = installationId }, bob.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await IntrospectAsync(jwt.EncodedToken)).Should().BeFalse("suspending ends the tokens the App holds");
        (await TokenAsync(installationId)).Body.GetProperty("error").GetString().Should().Be("invalid_grant", "and refuses new ones");

        (await SendAsync("/spark/apps/installations/unsuspend", new { id = installationId }, bob.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await IntrospectAsync(jwt.EncodedToken)).Should().BeFalse("resuming does not bring a withdrawn token back");
        var (_, fresh) = await TokenAsync(installationId);
        var freshToken = fresh.GetProperty("access_token").GetString()!;
        (await IntrospectAsync(freshToken)).Should().BeTrue();

        (await SendAsync("/spark/apps/installations/delete", new { id = installationId }, bob.Id)).Status.Should().Be(HttpStatusCode.NoContent);
        (await IntrospectAsync(freshToken)).Should().BeFalse("uninstalling revokes the grant");
        (await TokenAsync(installationId)).Body.GetProperty("error").GetString().Should().Be("invalid_grant");

        async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(string installation, string? scope = null)
        {
            var form = ClientAuth();
            form["grant_type"] = "client_credentials";
            form["installation_id"] = installation;
            if (scope is not null)
                form["scope"] = scope;
            return await PostFormAsync("/connect/token", form);
        }

        async Task<bool> IntrospectAsync(string accessToken)
        {
            var form = ClientAuth();
            form["token"] = accessToken;
            var (code, body) = await PostFormAsync("/connect/introspect", form);
            code.Should().Be(HttpStatusCode.OK, body.ToString());
            return body.GetProperty("active").GetBoolean();
        }

        Dictionary<string, string> ClientAuth() => new()
        {
            ["client_id"] = clientId,
            ["client_assertion_type"] = OidcClientAuthenticator.JwtBearerAssertionType,
            ["client_assertion"] = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = clientId,
                Audience = Issuer,
                Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString("N") },
                Expires = DateTime.UtcNow.AddMinutes(2),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            }),
        };
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostFormAsync(string url, Dictionary<string, string> form)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsync(url, new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task An_installation_token_is_a_member_of_the_groups_its_scopes_map_to_and_nothing_else_is()
    {
        var provider = new InstallationGroupMembership(
            new HttpContextAccessor { HttpContext = Context(ambient: false, ("installation_id", "in_1"), ("scope", "orders:write invoices:read")) },
            new AppPermissionGroups(new Dictionary<string, List<string>> { ["orders:read"] = ["OrderReaders"], ["admin:write"] = ["Administrators"] }));
        (await provider.GetCurrentUserGroupsAsync()).Should().Equal(["OrderReaders"], "orders:write covers the mapped orders:read; nothing maps invoices:read");

        var cookie = new InstallationGroupMembership(
            new HttpContextAccessor { HttpContext = Context(ambient: true, ("installation_id", "in_1"), ("scope", "orders:read")) },
            new AppPermissionGroups(new Dictionary<string, List<string>> { ["orders:read"] = ["OrderReaders"] }));
        (await cookie.GetCurrentUserGroupsAsync()).Should().BeEmpty("only a bearer installation token maps, never a cookie principal");

        var userToken = new InstallationGroupMembership(
            new HttpContextAccessor { HttpContext = Context(ambient: false, ("scope", "orders:read")) },
            new AppPermissionGroups(new Dictionary<string, List<string>> { ["orders:read"] = ["OrderReaders"] }));
        (await userToken.GetCurrentUserGroupsAsync()).Should().BeEmpty("a token without an installation is not an App's");

        static HttpContext Context(bool ambient, params (string Type, string Value)[] claims)
        {
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer")) };
            context.Features.Set<ISparkAuthenticatedSchemeFeature>(new SchemeFeature(new SparkCredentialScheme("test", ambient)));
            return context;
        }
    }

    private sealed class SchemeFeature(SparkCredentialScheme scheme) : ISparkAuthenticatedSchemeFeature
    {
        public SparkCredentialScheme Scheme { get; } = scheme;
    }
}
