using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Interceptors;

/// <summary>
/// Validation for the OIDC scope admin screen.
/// <para>
/// Scopes are the half of the configuration that decides what a token actually carries. A scope
/// name that does not match what a client lists, or one that is disabled, does not produce an
/// error anywhere in the flow — the authorization simply grants less than the screens showed.
/// </para>
/// </summary>
public sealed class OidcScopeInterceptors : IBeforeSave<OidcScope>
{
    public async ValueTask OnBeforeSaveAsync(OidcScope entity, SaveContext context)
    {
        if (string.IsNullOrWhiteSpace(entity.Name))
            throw new SparkValidationException("Scope name is required.", nameof(entity.Name));

        // Scope names travel space-delimited in the `scope` parameter and the `scope` claim, so a
        // name containing whitespace silently becomes two scopes, neither of which exists.
        if (entity.Name.Any(char.IsWhiteSpace))
            throw new SparkValidationException(
                $"Scope name '{entity.Name}' contains whitespace. Scopes are space-delimited on the wire, so it would be read as two.",
                nameof(entity.Name));

        foreach (var audience in entity.Audiences)
        {
            if (string.IsNullOrWhiteSpace(audience))
                throw new SparkValidationException("An audience cannot be empty.", nameof(entity.Audiences));
        }

        // Before the commit (#482): a pre-commit read still races two concurrent saves; true
        // uniqueness needs a compare-exchange reservation.
        // Not a session only when the interceptor is called by hand (unit tests of the rules above).
        if (context.Session is IAsyncDocumentSession session)
            await EnsureNameUniqueAsync(session, entity);
    }

    private static async Task EnsureNameUniqueAsync(IAsyncDocumentSession session, OidcScope entity)
    {
        var clash = await session.Query<OidcScope>()
            .Where(s => s.Name == entity.Name, exact: true)
            .ToListAsync();

        if (clash.Any(s => !string.Equals(s.Id, entity.Id, StringComparison.Ordinal)))
        {
            throw new SparkValidationException(
                $"Scope '{entity.Name}' already exists. Duplicates make the effective definition "
              + "whichever document the lookup returns first.",
                nameof(entity.Name));
        }
    }
}
