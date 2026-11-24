using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Webhooks;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>An App as its owner sees it.</summary>
public sealed record SparkAppView(
    string Slug, string Name, string? Description, string? HomepageUrl, string OwnerKind, string OwnerKey, string ClientId,
    bool Public, IReadOnlyList<string> Permissions, IReadOnlyList<string> Events, string? WebhookEndpointId, string? WebhookUrl,
    string? SetupUrl, IReadOnlyList<SparkAppKeyView> Keys, DateTime CreatedUtc);

public sealed record SparkAppKeyView(string KeyId, string Fingerprint, DateTime CreatedUtc);

/// <summary>An App as anyone who may install it sees it: no keys, no webhook.</summary>
public sealed record SparkAppPublicView(
    string Slug, string Name, string? Description, string? HomepageUrl, string OwnerKind, string OwnerKey,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Events);

/// <summary>A new App; <see cref="WebhookSecret"/> is its webhook's secret, shown this once (null without a webhook URL).</summary>
public sealed record SparkAppCreated(SparkAppView App, string? WebhookSecret);

/// <summary>A new key: the PKCS#8 PEM private key, shown this once. The site keeps only the public half.</summary>
public sealed record SparkAppKeyCreated(SparkAppKeyView Key, string PrivateKeyPem);

public sealed record SparkAppInput(
    string? OwnerKind, string? OwnerKey, string? Slug, string? Name, string? Description, string? HomepageUrl, string? WebhookUrl,
    string[]? Permissions, string[]? Events, bool? Public, string? SetupUrl);

/// <summary>Null leaves a field as it is; an empty <see cref="WebhookUrl"/> removes the webhook.</summary>
public sealed record SparkAppChange(
    string? Slug, string? Name, string? Description, string? HomepageUrl, string? WebhookUrl, string[]? Permissions,
    string[]? Events, bool? Public, string? SetupUrl);

/// <summary>
/// Apps, as their owners manage them (D29). The same rules as direct webhooks: the caller must manage the owner, someone
/// else's App answers NotFound, slugs and key ids are shape-checked before any load. An App's webhook gets a <c>ping</c>
/// when it is created and whenever its URL changes (§4.3 flow 5).
/// </summary>
internal sealed partial class SparkAppManager
{
    private static readonly Regex SlugShape = new("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex PermissionShape = new("^[a-z][a-z0-9_.-]{0,63}:(read|write)$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyIdShape = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly WebhookOwnerResolver owners;
    [Inject] private readonly WebhookEventCatalog catalog;
    [Inject] private readonly WebhookSecretProtector secrets;
    [Inject] private readonly WebhookEndpointManager endpoints;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly TimeProvider time;

    public async Task<WebhookResult<IReadOnlyList<SparkAppView>>> ListAsync(string? ownerKind, string? ownerKey, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ownerKind) || string.IsNullOrEmpty(ownerKey))
            return WebhookResult<IReadOnlyList<SparkAppView>>.Invalid("ownerKind and ownerKey are required.");
        var owner = new WebhookOwner(ownerKind, ownerKey);
        if (!await owners.CanManageAsync(owner, userId, cancellationToken))
            return WebhookResult<IReadOnlyList<SparkAppView>>.NotFound();

        using var session = store.OpenAsyncSession();
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(owner).Value, cancellationToken);
        if (registry is null || registry.AppIds.Count == 0)
            return WebhookResult<IReadOnlyList<SparkAppView>>.Ok([]);
        var apps = (await session.LoadAsync<SparkApp>(registry.AppIds, cancellationToken)).Values.Where(a => a is not null).ToList();
        var hooks = await session.LoadAsync<WebhookEndpoint>(apps.Where(a => a!.WebhookEndpointId is not null).Select(a => WebhookEndpoint.IdFor(a!.WebhookEndpointId!).Value), cancellationToken);
        return WebhookResult<IReadOnlyList<SparkAppView>>.Ok(apps
            .Select(a => View(a!, a!.WebhookEndpointId is { } ep ? hooks.GetValueOrDefault(WebhookEndpoint.IdFor(ep).Value) : null))
            .OrderBy(a => a.CreatedUtc)
            .ToList());
    }

    /// <summary>The owner's view to its managers; the public view to anyone else when the App is public; otherwise NotFound.</summary>
    public async Task<WebhookResult<object>> GetAsync(string? slug, string userId, CancellationToken cancellationToken)
    {
        if (slug is null || !SlugShape.IsMatch(slug))
            return WebhookResult<object>.NotFound();
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<SparkApp>(SparkApp.IdFor(slug).Value, cancellationToken) is not { } app)
            return WebhookResult<object>.NotFound();
        if (await owners.CanManageAsync(app.Owner, userId, cancellationToken))
        {
            var hook = app.WebhookEndpointId is { } ep ? await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(ep).Value, cancellationToken) : null;
            return WebhookResult<object>.Ok(View(app, hook));
        }
        return app.Public
            ? WebhookResult<object>.Ok(new SparkAppPublicView(app.Slug, app.Name, app.Description, app.HomepageUrl, app.OwnerKind, app.OwnerKey, app.Permissions, app.Events))
            : WebhookResult<object>.NotFound();
    }

    public async Task<WebhookResult<SparkAppCreated>> CreateAsync(SparkAppInput input, string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input.OwnerKind) || string.IsNullOrEmpty(input.OwnerKey))
            return WebhookResult<SparkAppCreated>.Invalid("ownerKind and ownerKey are required.");
        var owner = new WebhookOwner(input.OwnerKind, input.OwnerKey);
        if (!await owners.CanManageAsync(owner, userId, cancellationToken))
            return WebhookResult<SparkAppCreated>.NotFound();
        if (input.Slug is null || !SlugShape.IsMatch(input.Slug))
            return WebhookResult<SparkAppCreated>.Invalid("The slug is 1 to 40 lowercase letters, digits and dashes, starting and ending with a letter or digit.");
        if (ValidateText(input.Name, input.Description) is { } textError)
            return WebhookResult<SparkAppCreated>.Invalid(textError);
        if ((ValidateOptionalUrl(input.HomepageUrl, "homepage") ?? ValidateOptionalUrl(input.SetupUrl, "setup")) is { } urlError)
            return WebhookResult<SparkAppCreated>.Invalid(urlError);
        if (!string.IsNullOrEmpty(input.WebhookUrl) && endpoints.ValidateUrl(input.WebhookUrl) is { } hookError)
            return WebhookResult<SparkAppCreated>.Invalid(hookError);
        var permissions = (input.Permissions ?? []).Distinct(StringComparer.Ordinal).ToList();
        var events = (input.Events ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (ValidateGrants(permissions, events) is { } grantError)
            return WebhookResult<SparkAppCreated>.Invalid(grantError);

        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var registryId = WebhookOwnerRegistry.IdFor(owner);
        var registry = await session.LoadAsync<WebhookOwnerRegistry>(registryId.Value, cancellationToken);
        if (registry is null)
        {
            registry = new WebhookOwnerRegistry { Id = registryId, OwnerKind = owner.Kind, OwnerKey = owner.Key };
            await session.StoreAsync(registry, registryId.Value, cancellationToken);
        }
        if (registry.AppIds.Count >= options.Value.MaxAppsPerOwner)
            return WebhookResult<SparkAppCreated>.Invalid($"An owner has at most {options.Value.MaxAppsPerOwner} Apps.");
        if (await session.Advanced.ExistsAsync(SparkApp.IdFor(input.Slug).Value, cancellationToken))
            return WebhookResult<SparkAppCreated>.Conflict($"The slug '{input.Slug}' is taken.");

        var now = time.GetUtcNow().UtcDateTime;
        var client = await OidcMachineClients.CreateAsync(session, input.Name!.Trim(), userId, input.HomepageUrl?.Trim(), cancellationToken);
        var app = new SparkApp
        {
            Id = SparkApp.IdFor(input.Slug),
            Slug = input.Slug,
            Name = input.Name.Trim(),
            Description = input.Description?.Trim(),
            HomepageUrl = NullIfEmpty(input.HomepageUrl),
            OwnerKind = owner.Kind,
            OwnerKey = owner.Key,
            OidcApplicationId = client.Id.Value,
            ClientId = client.ClientId,
            Permissions = permissions,
            Events = events,
            Public = input.Public ?? false,
            SetupUrl = NullIfEmpty(input.SetupUrl),
            CreatedUtc = now,
            CreatedBy = userId,
        };
        // An empty change vector: the save fails if another request took the slug meanwhile.
        await session.StoreAsync(app, string.Empty, app.Id.Value, cancellationToken);
        registry.AppIds.Add(app.Id.Value);

        string? secret = null;
        WebhookEndpoint? hook = null;
        if (!string.IsNullOrEmpty(input.WebhookUrl))
            (hook, secret) = await AddWebhookAsync(session, app, input.WebhookUrl, cancellationToken);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            // The client id reservation outlives the failed save; the next save of that id takes it over (O17).
            return WebhookResult<SparkAppCreated>.Conflict("The slug was taken or the owner's Apps changed meanwhile; try again.");
        }
        return WebhookResult<SparkAppCreated>.Ok(new(View(app, hook), secret));
    }

    public async Task<WebhookResult<SparkAppCreated>> UpdateAsync(SparkAppChange change, string userId, CancellationToken cancellationToken)
        => await WithAppAsync(change.Slug, userId, async (session, app) =>
        {
            if (ValidateText(change.Name ?? app.Name, change.Description) is { } textError)
                return WebhookResult<SparkAppCreated>.Invalid(textError);
            if ((ValidateOptionalUrl(change.HomepageUrl, "homepage") ?? ValidateOptionalUrl(change.SetupUrl, "setup")) is { } urlError)
                return WebhookResult<SparkAppCreated>.Invalid(urlError);
            if (!string.IsNullOrEmpty(change.WebhookUrl) && endpoints.ValidateUrl(change.WebhookUrl) is { } hookError)
                return WebhookResult<SparkAppCreated>.Invalid(hookError);
            var permissions = change.Permissions?.Distinct(StringComparer.Ordinal).ToList() ?? app.Permissions;
            var events = change.Events?.Distinct(StringComparer.Ordinal).ToList() ?? app.Events;
            if (ValidateGrants(permissions, events) is { } grantError)
                return WebhookResult<SparkAppCreated>.Invalid(grantError);

            if (change.Name is not null) app.Name = change.Name.Trim();
            if (change.Description is not null) app.Description = NullIfEmpty(change.Description);
            if (change.HomepageUrl is not null) app.HomepageUrl = NullIfEmpty(change.HomepageUrl);
            if (change.SetupUrl is not null) app.SetupUrl = NullIfEmpty(change.SetupUrl);
            if (change.Public is { } isPublic) app.Public = isPublic;
            // D30: asking for more waits for each installer to accept; asking for less narrows every installation now.
            app.Permissions = permissions;
            app.Events = events;
            await AppInstallationManager.NarrowAsync(session, app, cancellationToken);

            var hook = app.WebhookEndpointId is { } ep ? await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(ep).Value, cancellationToken) : null;
            string? secret = null;
            if (change.WebhookUrl is { Length: 0 } && hook is not null)
            {
                session.Delete(hook);
                if (await session.LoadAsync<WebhookChain>(WebhookChain.IdFor(hook.PublicId).Value, cancellationToken) is { } chain)
                    session.Delete(chain);
                app.WebhookEndpointId = null;
                hook = null;
            }
            else if (change.WebhookUrl is { Length: > 0 } url && hook is null)
            {
                (hook, secret) = await AddWebhookAsync(session, app, url, cancellationToken);
            }
            else if (hook is not null)
            {
                hook.Events = [.. app.Events];
                if (change.WebhookUrl is { Length: > 0 } newUrl && newUrl.Trim() != hook.Url)
                {
                    hook.Url = newUrl.Trim();
                    if (hook.Enabled)
                        await endpoints.SchedulePingAsync(session, hook, cancellationToken);
                }
            }

            if (change.Name is not null && await session.LoadAsync<OidcApplication>(app.OidcApplicationId, cancellationToken) is { } client)
                client.DisplayName = app.Name;
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<SparkAppCreated>.Ok(new(View(app, hook), secret));
        }, cancellationToken);

    /// <summary>Deletes the App, its installations, its webhook (the log expires on its own) and its client, whose tokens are revoked.</summary>
    public async Task<WebhookResult<bool>> DeleteAsync(string? slug, string userId, CancellationToken cancellationToken)
    {
        string? clientId = null;
        var result = await WithAppAsync(slug, userId, async (session, app) =>
        {
            if (await session.LoadAsync<WebhookOwnerRegistry>(WebhookOwnerRegistry.IdFor(app.Owner).Value, cancellationToken) is { } registry)
                // Ignoring case: an App listed before #536 is "SparkApps/…" there, its loaded id lower case.
                registry.AppIds.RemoveAll(id => string.Equals(id, app.Id.Value, StringComparison.OrdinalIgnoreCase));
            await AppInstallationManager.RemoveAllAsync(session, app, cancellationToken);
            if (app.WebhookEndpointId is { } ep)
            {
                session.Delete(WebhookEndpoint.IdFor(ep).Value);
                session.Delete(WebhookChain.IdFor(ep).Value);
            }
            session.Delete(app);
            await session.SaveChangesAsync(cancellationToken);
            clientId = app.OidcApplicationId;
            return WebhookResult<bool>.Ok(true);
        }, cancellationToken);
        if (clientId is not null)
            await OidcMachineClients.DeleteAsync(store, clientId, cancellationToken);
        return result;
    }

    /// <summary>
    /// A new RS256 key pair (GitHub's "Generate a private key"): the public half joins the client's JWKS, the private
    /// half is returned this once. Up to <see cref="SparkAppsOptions.MaxKeysPerApp"/>, so a key can be rotated.
    /// </summary>
    public Task<WebhookResult<SparkAppKeyCreated>> GenerateKeyAsync(string? slug, string userId, CancellationToken cancellationToken)
        => WithAppAsync(slug, userId, async (session, app) =>
        {
            if (app.Keys.Count >= options.Value.MaxKeysPerApp)
                return WebhookResult<SparkAppKeyCreated>.Invalid($"An App holds at most {options.Value.MaxKeysPerApp} keys; delete one first.");
            using var rsa = RSA.Create(2048);
            var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var parameters = rsa.ExportParameters(false);
            var jwk = new JsonObject
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = SecurityAlgorithms.RsaSha256,
                ["kid"] = keyId,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            };
            var key = new SparkAppKey
            {
                KeyId = keyId,
                Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())),
                PublicJwk = jwk.ToJsonString(),
                CreatedUtc = time.GetUtcNow().UtcDateTime,
            };
            app.Keys.Add(key);
            if (!await SyncJwksAsync(session, app, cancellationToken))
                return WebhookResult<SparkAppKeyCreated>.NotFound();
            await session.SaveChangesAsync(cancellationToken);
            return WebhookResult<SparkAppKeyCreated>.Ok(new(KeyView(key), rsa.ExportPkcs8PrivateKeyPem()));
        }, cancellationToken);

    /// <summary>Removes a key; assertions it signs are refused from then on.</summary>
    public Task<WebhookResult<SparkAppView>> DeleteKeyAsync(string? slug, string? keyId, string userId, CancellationToken cancellationToken)
        => WithAppAsync(slug, userId, async (session, app) =>
        {
            if (keyId is null || !KeyIdShape.IsMatch(keyId) || app.Keys.RemoveAll(k => k.KeyId == keyId) == 0)
                return WebhookResult<SparkAppView>.NotFound();
            if (!await SyncJwksAsync(session, app, cancellationToken))
                return WebhookResult<SparkAppView>.NotFound();
            await session.SaveChangesAsync(cancellationToken);
            var hook = app.WebhookEndpointId is { } ep ? await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(ep).Value, cancellationToken) : null;
            return WebhookResult<SparkAppView>.Ok(View(app, hook));
        }, cancellationToken);

    private static async Task<bool> SyncJwksAsync(IAsyncDocumentSession session, SparkApp app, CancellationToken cancellationToken)
    {
        if (await session.LoadAsync<OidcApplication>(app.OidcApplicationId, cancellationToken) is not { } client)
            return false;
        var keys = new JsonArray(app.Keys.Select(k => (JsonNode?)JsonNode.Parse(k.PublicJwk)).ToArray());
        client.Jwks = app.Keys.Count == 0 ? null : new JsonObject { ["keys"] = keys }.ToJsonString();
        return true;
    }

    private async Task<(WebhookEndpoint Hook, string Secret)> AddWebhookAsync(IAsyncDocumentSession session, SparkApp app, string url, CancellationToken cancellationToken)
    {
        var secret = WebhookSecret.Generate();
        var hook = new WebhookEndpoint
        {
            Id = WebhookEndpoint.IdFor("ep_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))),
            OwnerKind = app.OwnerKind,
            OwnerKey = app.OwnerKey,
            AppSlug = app.Slug,
            Url = url.Trim(),
            Description = "Webhook of the App " + app.Slug,
            Events = [.. app.Events],
            ProtectedSecret = secrets.Protect(secret),
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
        await session.StoreAsync(hook, hook.Id.Value, cancellationToken);
        app.WebhookEndpointId = hook.PublicId;
        await endpoints.SchedulePingAsync(session, hook, cancellationToken);
        return (hook, secret.ToString());
    }

    private async Task<WebhookResult<T>> WithAppAsync<T>(string? slug, string userId, Func<IAsyncDocumentSession, SparkApp, Task<WebhookResult<T>>> action, CancellationToken cancellationToken)
    {
        if (slug is null || !SlugShape.IsMatch(slug))
            return WebhookResult<T>.NotFound();
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var app = await session.LoadAsync<SparkApp>(SparkApp.IdFor(slug).Value, cancellationToken);
        if (app is null || !await owners.CanManageAsync(app.Owner, userId, cancellationToken))
            return WebhookResult<T>.NotFound();
        try
        {
            return await action(session, app);
        }
        catch (ConcurrencyException)
        {
            return WebhookResult<T>.Conflict("The App changed meanwhile; reload and try again.");
        }
    }

    /// <summary>
    /// Every permission is <c>resource:read|write</c>; every event is in the catalog (not <c>*</c>: an App asks for what it
    /// uses), and its permission is covered by the App's (<c>write</c> covers <c>read</c>).
    /// </summary>
    private string? ValidateGrants(List<string> permissions, List<string> events)
    {
        if (permissions.Count > 50 || events.Count > 100)
            return "Too many permissions or events.";
        if (permissions.FirstOrDefault(p => !PermissionShape.IsMatch(p)) is { } badPermission)
            return $"'{badPermission}' is not a permission; use resource:read or resource:write.";
        foreach (var name in events)
        {
            if (catalog.Find(name) is not { } descriptor)
                return $"Unknown event: {name}";
            if (descriptor.Permission is { } needed && !Covers(permissions, needed))
                return $"The event '{name}' needs the permission '{needed}'.";
        }
        return null;
    }

    internal static bool Covers(IEnumerable<string> granted, string needed)
    {
        if (granted.Contains(needed, StringComparer.Ordinal))
            return true;
        return needed.EndsWith(":read", StringComparison.Ordinal)
            && granted.Contains(needed[..^":read".Length] + ":write", StringComparer.Ordinal);
    }

    private static string? ValidateText(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80)
            return "The name is 1 to 80 characters.";
        if (description is { Length: > 1000 })
            return "The description is at most 1000 characters.";
        return null;
    }

    private static string? ValidateOptionalUrl(string? url, string what)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        return url.Length <= 2048 && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(uri.UserInfo)
            ? null
            : $"The {what} URL must be an absolute http(s) URL.";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SparkAppKeyView KeyView(SparkAppKey k) => new(k.KeyId, k.Fingerprint, k.CreatedUtc);

    private static SparkAppView View(SparkApp a, WebhookEndpoint? hook) => new(
        a.Slug, a.Name, a.Description, a.HomepageUrl, a.OwnerKind, a.OwnerKey, a.ClientId, a.Public, a.Permissions, a.Events,
        a.WebhookEndpointId, hook?.Url, a.SetupUrl, a.Keys.Select(KeyView).ToList(), a.CreatedUtc);
}
