using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// What disabling an application or an API resource takes down with it (<c>docs/identity_provider_platform_PRD.md</c>
/// D9, G26). Without it, <c>Enabled = false</c> stopped new sign-ins while every token already issued kept working
/// until it expired.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>An application:</b> every valid token it holds (codes, access and refresh tokens, device codes, pushed
/// requests) and every grant its users gave it. Re-enabling it does not bring them back: its users consent again.</item>
/// <item><b>An API resource:</b> every valid token carrying one of its scopes. Grants stay: a disabled scope is no
/// longer issued, and re-enabling the resource honours the consent the users already gave.</item>
/// </list>
/// Both are set-based patches with every value a query parameter; the collection names come from the conventions
/// and pass <see cref="RqlIdentifier.Collection"/>. The wait is bounded: a patch that outlives it keeps running on the
/// server, and the access-token check (<see cref="AccessTokens"/>) reads the patched status, not this call.
/// </remarks>
internal static class OidcDisableCascade
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public static async Task RevokeApplicationAsync(IDocumentStore store, string applicationId, CancellationToken ct = default)
    {
        var tokens = RqlIdentifier.Collection(store.Conventions.GetCollectionName(typeof(OidcToken)));
        var grants = RqlIdentifier.Collection(store.Conventions.GetCollectionName(typeof(OidcGrant)));
        var parameters = new Parameters { ["app"] = applicationId, ["valid"] = "valid", ["revoked"] = "revoked" };

        await RunAsync(store, $"from {tokens} where ApplicationId = $app and Status = $valid update {{ this.Status = $revoked; }}", parameters, ct);
        await RunAsync(store, $"from {grants} where ApplicationId = $app and Status = $valid update {{ this.Status = $revoked; }}", parameters, ct);
    }

    public static async Task RevokeScopesAsync(IDocumentStore store, IReadOnlyCollection<string> scopes, CancellationToken ct = default)
    {
        if (scopes.Count == 0)
            return;
        var tokens = RqlIdentifier.Collection(store.Conventions.GetCollectionName(typeof(OidcToken)));
        var parameters = new Parameters { ["scopes"] = scopes.ToArray(), ["valid"] = "valid", ["revoked"] = "revoked" };

        await RunAsync(store, $"from {tokens} where Status = $valid and Scopes in ($scopes) update {{ this.Status = $revoked; }}", parameters, ct);
    }

    private static async Task RunAsync(IDocumentStore store, string rql, Parameters parameters, CancellationToken ct)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = rql, QueryParameters = parameters }), token: ct);
        try { await operation.WaitForCompletionAsync(Wait); }
        catch (TimeoutException) { /* still running server-side; see remarks */ }
    }
}
