using System.Globalization;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace CodeCoverage.Actions;

/// <summary>
/// One accounts page per forge — D4 made navigable. Resolved by name, <c>ForgeAccounts</c> +
/// <c>Actions</c>, because <c>ForgeAccounts.json</c> declares no <c>clrType</c>.
/// <para>
/// The forge arrives as the program unit's <c>objectId</c>, so the sidebar entry
/// <em>is</em> the scope: there is no merged list to filter down from, and no screen that has to
/// render three different meanings of "you may manage this" in one table.
/// </para>
/// </summary>
public partial class ForgeAccountsActions
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IMyAccountsService myAccounts;
    [Inject] private readonly IRequestCultureResolver culture;
    [Inject] private readonly ITranslationsLoader translations;

    /// <summary>
    /// Scaffolds the page for one forge.
    /// </summary>
    /// <param name="id">
    /// The forge, in the canonical lowercase spelling shared with document ids and routes.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>An unrecognised forge returns <c>null</c>, which the framework answers as 404.</b> The
    /// tempting alternative — fall back to GitHub, the only implemented forge — would make
    /// <c>/po/forge-accounts/gitlab</c> silently render GitHub's accounts under a GitLab heading.
    /// That is the precise failure mode this whole issue exists to remove, and it would be
    /// reintroduced here by a one-line convenience.
    /// <para>
    /// The counts come from the same <see cref="IMyAccountsService"/> call the grid below them
    /// makes, scoped to the same forge, so the header cannot disagree with the rows it introduces.
    /// </para>
    /// </remarks>
    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        if (!ForgeProviders.TryParse(id, out var provider))
            return null;

        var obj = manager.GetPersistentObject("ForgeAccounts");
        var canonical = provider.ToCanonicalString();

        obj["Provider"].Value = canonical;

        // The forge's own name is a proper noun and never translated; only the sentence around it
        // is. One key for every forge rather than one key each, so adding a forge adds no strings.
        var lang = culture.GetCurrentCulture();
        var template = translations.Resolve("app.forgeAccountsTitle")?.GetValue(lang);
        var title = string.IsNullOrEmpty(template)
            ? provider.ToString()
            : string.Format(CultureInfo.InvariantCulture, template, provider.ToString());
        obj["Title"].Value = title;
        obj.Breadcrumb = title;

        var accounts = await myAccounts.GetAsync(CancellationToken.None, provider: provider);
        obj["AccountCount"].Value = accounts.Accounts.Length;
        obj["RepoCount"].Value = accounts.Accounts.Sum(a => a.RepoCount);

        return obj;
    }
}
