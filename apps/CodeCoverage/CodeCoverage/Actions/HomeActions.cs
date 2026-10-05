using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace CodeCoverage.Actions;

/// <summary>
/// The composed Home page. Resolved by name — <c>Home</c> + <c>Actions</c> — because
/// <c>Home.json</c> declares no <c>clrType</c>, so there is no CLR type to resolve over.
/// <para>
/// No base class, and the load hook's signature is duck-typed: a wrong shape throws loudly at
/// first request rather than silently 404ing, and no class at all means 404.
/// </para>
/// </summary>
public partial class HomeActions
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IMyAccountsService myAccounts;
    [Inject] private readonly ITranslationsLoader translations;
    [Inject] private readonly IRequestCultureResolver culture;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>
    /// Scaffolds the page and fills it. The id is ignored: there is exactly one Home.
    /// </summary>
    /// <remarks>
    /// The counts come from the same <see cref="IMyAccountsService"/> the accounts grid below
    /// them uses, so the header cannot disagree with the rows it introduces. For an anonymous
    /// caller that service yields nothing, and the counts are hidden rather than shown as zero —
    /// "0 accounts" reads as a fact about the visitor's GitHub rather than about their being
    /// signed out. The hiding is <c>security.json</c>'s: <c>QueryRead/Home/AccountCount</c> and
    /// <c>RepoCount</c> are denied to the anonymous role, so the two attributes never reach a
    /// visitor at all (#264). This hook only skips computing them.
    /// </remarks>
    public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = manager.GetPersistentObject("Home");
        var lang = culture.GetCurrentCulture();

        // The page title is the breadcrumb, set here directly: Home.json declares no breadcrumb
        // template, and no Title attribute to feed one.
        obj.Breadcrumb = Translate("app.welcomeTitle", lang);

        var isAuthenticated = httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;
        if (!isAuthenticated)
        {
            obj["Subtitle"].Value =
                $"{Translate("app.welcomeSubtitle", lang)} {Translate("app.signInPrompt", lang)}".Trim();
            return obj;
        }

        obj["Subtitle"].Value = Translate("app.welcomeSubtitle", lang);

        var accounts = await myAccounts.GetAsync(CancellationToken.None);
        obj["AccountCount"].Value = accounts.Accounts.Length;
        obj["RepoCount"].Value = accounts.Accounts.Sum(a => a.RepoCount);

        return obj;
    }

    private string Translate(string key, string culture)
        => translations.Resolve(key)?.GetValue(culture) ?? string.Empty;
}
