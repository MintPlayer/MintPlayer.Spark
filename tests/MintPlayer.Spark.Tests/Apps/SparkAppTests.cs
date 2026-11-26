using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Tests.IdentityProvider;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 M4 (<c>docs/apps_webhooks_PRD.md</c> D29): an App is an identity-provider client with its own keys and a webhook
/// that gets a <c>ping</c> when it is created or moved.
/// </summary>
public class SparkAppTests : AppsTestHost
{
    protected override void ConfigureAppsBuilder(AppsBuilder apps) => apps.EnableApps();

    protected override void ConfigureSpark(ISparkBuilder spark) => spark.AddIdentityProvider(o =>
    {
        o.Issuer = "https://idp.example.com";
        o.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
    });

    private async Task<(HttpStatusCode Status, JsonElement Body)> CreateAppAsync(string userId, WebhookOwner owner, string slug, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["ownerKind"] = owner.Kind,
            ["ownerKey"] = owner.Key,
            ["slug"] = slug,
            ["name"] = "Shipping bot",
            ["webhookUrl"] = ReceiverUrl,
            ["permissions"] = new[] { "orders:read" },
            ["events"] = new[] { "order.shipped" },
        };
        if (extra is not null)
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                body[property.Name] = property.Value;
        return await SendAsync("/spark/apps/apps", body, userId);
    }

    [Fact]
    public async Task A_new_App_is_a_private_key_jwt_client_and_its_webhook_receives_a_signed_ping()
    {
        var ann = await SeedUserAsync("ann");

        var (status, body) = await CreateAppAsync(ann.Id!, WebhookOwner.User(ann.Id!), "shipping-bot");

        status.Should().Be(HttpStatusCode.OK, "ann registers an App on her own account: " + body);
        var app = body.GetProperty("app");
        var secret = body.GetProperty("webhookSecret").GetString()!;
        secret.Should().StartWith("whsec_", "the webhook secret is shown this once");
        var endpointId = app.GetProperty("webhookEndpointId").GetString()!;

        await UntilSettledAsync(endpointId, 1);
        var ping = Receiver.Received.Should().ContainSingle("creating the App pings its webhook").Which;
        ping.Json.GetProperty("type").GetString().Should().Be("ping");
        ping.Json.GetProperty("data").GetProperty("app").GetString().Should().Be("shipping-bot");
        new WebhookVerifier(new WebhookVerifierOptions { Secrets = { secret } }).Verify(h => ping.Header(h), ping.Body).IsValid
            .Should().BeTrue("the ping is signed with the App's webhook secret");

        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<SparkApp>(SparkApp.IdFor("shipping-bot").Value);
        var client = await session.LoadAsync<OidcApplication>(stored!.OidcApplicationId);
        client!.ClientId.Should().Be(app.GetProperty("clientId").GetString(), "the App's client id is its identity provider client's");
        client.AllowedGrantTypes.Should().Equal(["client_credentials"], "an App only ever gets machine tokens");
        client.TokenEndpointAuthMethod.Should().Be("private_key_jwt", "an App authenticates with a key, never a secret");
        client.Secrets.Should().BeEmpty("there is no client secret to leak");
    }

    [Fact]
    public async Task A_generated_key_signs_client_assertions_the_clients_JWKS_accepts_until_it_is_deleted()
    {
        var ann = await SeedUserAsync("ann");
        (await CreateAppAsync(ann.Id!, WebhookOwner.User(ann.Id!), "keyed")).Status.Should().Be(HttpStatusCode.OK);

        var (status, body) = await SendAsync("/spark/apps/apps/keys/generate", new { slug = "keyed" }, ann.Id);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var keyId = body.GetProperty("key").GetProperty("keyId").GetString()!;
        var pem = body.GetProperty("privateKeyPem").GetString()!;
        pem.Should().StartWith("-----BEGIN PRIVATE KEY-----", "the private key is handed over as PKCS#8 PEM");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "the-client",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256),
        });

        (await ValidatesAsync(assertion)).Should().BeTrue("the client's JWKS holds the public half");

        (await SendAsync("/spark/apps/apps/keys/delete", new { slug = "keyed", keyId }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        (await ValidatesAsync(assertion)).Should().BeFalse("a deleted key no longer authenticates the App");

        async Task<bool> ValidatesAsync(string token)
        {
            using var session = Store.OpenAsyncSession();
            var app = await session.LoadAsync<SparkApp>(SparkApp.IdFor("keyed").Value);
            var client = await session.LoadAsync<OidcApplication>(app!.OidcApplicationId);
            if (client!.Jwks is null)
                return false;
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKeys = new JsonWebKeySet(client.Jwks).GetSigningKeys(),
            });
            return result.IsValid;
        }
    }

    [Fact]
    public async Task An_event_needs_its_permission_write_covers_read_and_a_slug_is_taken_once()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);

        (await CreateAppAsync(ann.Id!, owner, "no-grant", new { permissions = Array.Empty<string>() })).Status
            .Should().Be(HttpStatusCode.BadRequest, "order.shipped needs orders:read");
        (await CreateAppAsync(ann.Id!, owner, "writer", new { permissions = new[] { "orders:write" } })).Status
            .Should().Be(HttpStatusCode.OK, "orders:write covers orders:read");
        (await CreateAppAsync(ann.Id!, owner, "all", new { events = new[] { "*" } })).Status
            .Should().Be(HttpStatusCode.BadRequest, "an App names the events it uses");
        (await CreateAppAsync(ann.Id!, owner, "Bad_Slug")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await CreateAppAsync(ann.Id!, owner, "bad-permission", new { permissions = new[] { "orders" } })).Status.Should().Be(HttpStatusCode.BadRequest);

        var bob = await SeedUserAsync("bob");
        (await CreateAppAsync(bob.Id!, WebhookOwner.User(bob.Id!), "writer")).Status.Should().Be(HttpStatusCode.Conflict, "slugs are unique across the site");
    }

    [Fact]
    public async Task Others_see_a_public_App_without_its_secrets_and_a_private_one_not_at_all()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        (await CreateAppAsync(ann.Id!, WebhookOwner.User(ann.Id!), "private-app")).Status.Should().Be(HttpStatusCode.OK);
        (await CreateAppAsync(ann.Id!, WebhookOwner.User(ann.Id!), "public-app", new { @public = true })).Status.Should().Be(HttpStatusCode.OK);

        (await GetAsync("/spark/apps/apps/app?slug=private-app", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        var (status, body) = await GetAsync("/spark/apps/apps/app?slug=public-app", bob.Id);
        status.Should().Be(HttpStatusCode.OK);
        body.TryGetProperty("clientId", out _).Should().BeFalse("the public view carries no client id, keys or webhook");
        body.TryGetProperty("webhookUrl", out _).Should().BeFalse();
        body.GetProperty("permissions")[0].GetString().Should().Be("orders:read", "an installer sees what the App asks for");

        (await SendAsync("/spark/apps/apps/update", new { slug = "public-app", name = "Mine now" }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/apps/apps/keys/generate", new { slug = "public-app" }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/apps/apps/delete", new { slug = "public-app" }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Apps_webhook_is_managed_through_the_App_and_moving_it_pings_the_new_URL()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        var (_, created) = await CreateAppAsync(ann.Id!, owner, "mover");
        var endpointId = created.GetProperty("app").GetProperty("webhookEndpointId").GetString()!;
        await UntilSettledAsync(endpointId, 1);

        var (_, list) = await GetAsync($"/spark/apps/webhooks/endpoints?ownerKind={owner.Kind}&ownerKey={owner.Key}", ann.Id);
        list.GetArrayLength().Should().Be(0, "an App's webhook is not one of the owner's direct webhooks");
        (await SendAsync("/spark/apps/webhooks/endpoints/update", new { id = endpointId, url = "https://elsewhere.example.com/" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.BadRequest, "the App decides its webhook's URL");
        (await SendAsync("/spark/apps/webhooks/endpoints/delete", new { id = endpointId }, ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest);

        (await SendAsync("/spark/apps/apps/update", new { slug = "mover", webhookUrl = "https://hooks.example.com/moved" }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        await UntilSettledAsync(endpointId, 2);
        Receiver.Received.Last().Url.Should().Be("https://hooks.example.com/moved", "a moved webhook is pinged at its new URL");

        string clientDocumentId;
        using (var before = Store.OpenAsyncSession())
            clientDocumentId = (await before.LoadAsync<SparkApp>(SparkApp.IdFor("mover").Value))!.OidcApplicationId;

        (await SendAsync("/spark/apps/apps/delete", new { slug = "mover" }, ann.Id)).Status.Should().Be(HttpStatusCode.NoContent);
        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(WebhookEndpoint.IdPrefix + endpointId)).Should().BeFalse("the webhook goes with the App");
        (await session.Advanced.ExistsAsync(clientDocumentId)).Should().BeFalse("so does its identity provider client");
        (await session.Advanced.ExistsAsync(SparkApp.IdFor("mover").Value)).Should().BeFalse();
    }

    [Fact]
    public void EnableApps_without_the_identity_provider_stops_startup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MintPlayer.Spark.Messaging.IMessageOutbox, NoOutbox>();
        services.AddSingleton<MintPlayer.Spark.Apps.Endpoints.SparkAppsFeature>();
        services.AddOptions<SparkAppsOptions>();

        var act = () => SparkAppsExtensions.ValidateStartup(services.BuildServiceProvider());

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddIdentityProvider*");
    }

    private sealed class NoOutbox : MintPlayer.Spark.Messaging.IMessageOutbox
    {
        public Task EnqueueAsync<TMessage>(TMessage message, MintPlayer.Spark.Messaging.Abstractions.BroadcastOptions? options = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
