using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Actions;

/// <summary>One row of the <c>my-passkeys</c> query; its property names are the <c>PasskeyRow</c> attributes.</summary>
/// <param name="Id">The base64url credential id, which the row actions send back as the selection.</param>
internal sealed record PasskeyRow(string Id, string Name, DateTimeOffset Created, bool Synced);

/// <summary>
/// Backs <c>Custom.MyPasskeys</c>, the query of the virtual <c>PasskeyRow</c> type
/// (<c>App_Data/Model/PasskeyRow.json</c>), shown on the <c>Passkeys</c> page.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>The method takes no <c>CustomQueryArgs</c>, deliberately.</b> The query is a sub-query of the
/// page, so the executor would hand it the page as <c>Parent</c>; a user id read from there would be
/// an IDOR. With no argument there is nothing to read: the rows come from the request principal
/// alone, through <see cref="ISparkPasskeyAccount"/> (PRD D2).
/// </para>
/// <para>
/// The selection a row action receives is rebuilt by re-running this query, so the actions can
/// only ever be handed the caller's own passkeys too.
/// </para>
/// </remarks>
internal sealed partial class PasskeyRowActions : ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Rows are GENERATED from the caller's own account rather than filtered afterwards: " +
        "PasskeyRowActions.MyPasskeys takes no arguments and reads only ISparkPasskeyAccount.ListAsync " +
        "(Identity/SparkPasskeyAccount.cs), which resolves the user from the request principal and returns " +
        "nothing for an anonymous caller or when passkeys are disabled. Nothing in the request can name " +
        "another user, so there is no post-filter to apply; the library's security.json grants " +
        "Query/PasskeyRow to @authenticated only.";

    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPasskeyAccount passkeys;

    /// <summary>The signed-in user's passkeys, newest first by the query's sort. Metadata only.</summary>
    public async Task<IQueryable<PasskeyRow>> MyPasskeys()
    {
        var list = await passkeys.ListAsync();
        if (list.Count == 0)
            return Enumerable.Empty<PasskeyRow>().AsQueryable();

        var unnamed = manager.GetTranslatedMessage("auth.passkeyUnnamed");
        return list
            .Select(p => new PasskeyRow(
                PasskeyEndpoints.EncodeCredentialId(p.CredentialId),
                string.IsNullOrWhiteSpace(p.Name) ? unnamed : p.Name,
                p.CreatedAt,
                p.IsBackedUp))
            .ToList()
            .AsQueryable();
    }
}
