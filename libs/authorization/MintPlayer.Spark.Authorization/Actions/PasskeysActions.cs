using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Actions;

/// <summary>Names the passkeys page shares between its model files, its actions and the client's routes.</summary>
internal static class SparkPasskeysPage
{
    /// <summary>The page's fixed object id: <c>/po/passkeys/me</c>. The id is never a user id (PRD D1).</summary>
    internal const string ObjectId = "me";

    /// <summary>The sub-query's alias, which <c>RefreshQuery</c> takes (not its name).</summary>
    internal const string QueryAlias = "my-passkeys";
}

/// <summary>
/// The signed-in user's passkeys page, a virtual persistent object (<c>App_Data/Model/Passkeys.json</c>,
/// no <c>clrType</c>) resolved by name, <c>Passkeys</c> + <c>Actions</c>, like CodeCoverage's
/// <c>ForgeAccountsActions</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>No application may declare a class of this name</b> (nor <c>PasskeyRowActions</c>): resolution
/// by name refuses a duplicate simple name across loaded assemblies (PRD §9 S2).
/// </remarks>
internal sealed partial class PasskeysActions
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPasskeyAccount passkeys;

    /// <summary>Scaffolds the page for whoever is signed in.</summary>
    /// <param name="id">Ignored: there is one page, the caller's own. The route says <c>me</c>.</param>
    /// <remarks>
    /// Null, which the framework answers as 404, when nobody is signed in or the application has
    /// <see cref="Configuration.SparkPasskeys.Disabled"/>: a page that lists nothing and offers an
    /// enrollment that cannot run is worse than no page.
    /// </remarks>
    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        if (!await passkeys.IsAvailableAsync())
            return null;

        var obj = await manager.GetPersistentObjectAsync("Passkeys");
        obj.Id = SparkPasskeysPage.ObjectId;
        obj.Breadcrumb = manager.GetTranslatedMessage("auth.passkeysTitle");
        obj["Description"].Value = manager.GetTranslatedMessage("auth.passkeysDescription");
        return obj;
    }
}
