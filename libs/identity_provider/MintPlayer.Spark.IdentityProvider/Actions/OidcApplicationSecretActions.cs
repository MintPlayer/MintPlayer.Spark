using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>One row of an application's <c>oidc-application-secrets</c> sub-query: everything about a secret but the secret (D5).</summary>
/// <param name="Id">The secret's id (<see cref="ClientSecret.SecretId"/>), which <c>RevokeSecret</c> receives.</param>
public sealed record OidcApplicationSecretRow(string Id, string? Description, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt);

/// <summary>Backs <c>Custom.Secrets</c>, the secrets list on an application's page (D5).</summary>
internal sealed partial class OidcApplicationSecretRowActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Rows come from the parent OidcApplication only, which the executor re-loads through the members-only " +
        "read path first. A row carries the secret's id, note and dates; the hash is never projected.";

    public const string QueryAlias = "oidc-application-secrets";

    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IQueryable<OidcApplicationSecretRow>> Secrets(CustomQueryArgs args)
    {
        args.EnsureParent(nameof(OidcApplication));
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<OidcApplication>(id) is not { } app)
            return Enumerable.Empty<OidcApplicationSecretRow>().AsQueryable();

        return app.Secrets
            .Where(s => s.SecretId is not null)
            .Select(s => new OidcApplicationSecretRow(s.SecretId!, s.Description, s.CreatedAt, s.ExpiresAt, s.LastUsedAt))
            .ToList()
            .AsQueryable();
    }
}
