using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>
/// Validation for the OIDC scope admin screen.
/// <para>
/// Scopes are the half of the configuration that decides what a token actually carries. A scope
/// name that does not match what a client lists, or one that is disabled, does not produce an
/// error anywhere in the flow — the authorization simply grants less than the screens showed.
/// </para>
/// </summary>
public partial class OidcScopeActions : DefaultPersistentObjectActions<OidcScope>
{
    public override async Task OnBeforeSaveAsync(PersistentObject obj, OidcScope entity)
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

        // Before the write too (#467 finding): checked only afterwards, a duplicate was refused with a
        // 400 while staying stored. The check after the write still catches the loser of a race.
        if (requestSession is not null)
            await EnsureNameUniqueAsync(requestSession, entity);

        await base.OnBeforeSaveAsync(obj, entity);
    }

    // Null only when the actions are built by hand (unit tests); the check after the write remains.
    [Inject] private readonly IAsyncDocumentSession requestSession;

    public override async Task<OidcScope> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
    {
        var entity = await base.OnSaveAsync(session, obj);
        await EnsureNameUniqueAsync(session, entity);
        return entity;
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
