using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// Contributions M6 in the SPA, on QnA's question form: a translator who is not the author adds a
/// version of a translation, edits it, sees the attribution line, opens the history through its link
/// (the slot filters shown as chips), a moderator reverts to it from the version's page, and the
/// translator withdraws it and is told what is shown now. The API-level rules are
/// <see cref="QnAContributionsTests"/>; this is where a missing provider, renderer or right in the
/// client wiring would show.
/// </summary>
/// <remarks>
/// Request budget (the shared rate-limit bucket): two browser sessions and a handful of API calls.
/// </remarks>
[Collection(QnAE2ECollection.Name)]
public class QnAContributionsBrowserTests
{
    private const int Timeout = 15_000;
    private readonly QnAE2ECollectionFixture fixture;

    public QnAContributionsBrowserTests(QnAE2ECollectionFixture fixture) => this.fixture = fixture;

    private static string Encoded(string id) => Uri.EscapeDataString(id);

    /// <summary>The row editor of an AsDetail collection: a modal titled "Edit {label}".</summary>
    private static ILocator RowEditor(IPage page)
        => page.Locator(".modal.show, [role='dialog']").Filter(new() { HasTextString = "Edit Translations" }).Last;

    /// <remarks>
    /// <para>
    /// The page's Save is the edit form's submit button. It used to be found as the <em>last</em>
    /// button named "Save", but the row editor's footer has one too, later in the DOM: while that
    /// modal was still closing on a slow runner, the click landed on the modal's Save, no save was
    /// sent, and the test timed out waiting for the navigation (CI runs 37031951328, 37059767321).
    /// So the row editor must be gone first, and the button is selected by being the submit.
    /// </para>
    /// <para>
    /// Waits for the <c>/po/update</c> answer before the navigation, so a refused save fails with the
    /// server's status and body instead of a navigation timeout.
    /// </para>
    /// </remarks>
    private static async Task SaveFormAsync(IPage page, string questionId)
    {
        await RowEditor(page).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = Timeout });
        var saved = page.WaitForResponseAsync(
            response => response.Request.Method == "POST" && response.Url.Contains("/po/update", StringComparison.Ordinal),
            new() { Timeout = Timeout });
        await FormSave(page).ClickAsync();

        IResponse response;
        try { response = await saved; }
        catch (TimeoutException) { throw new TimeoutException($"Clicking Save sent no /po/update within {Timeout} ms."); }
        if (!response.Ok)
            throw new InvalidOperationException($"/po/update answered {response.Status}: {await response.TextAsync()}");

        await page.WaitForURLAsync(url => url.EndsWith("/po/question/" + Encoded(questionId), StringComparison.Ordinal), new() { Timeout = Timeout });
    }

    /// <summary>The edit page's own Save: the form's submit button, never the row editor's footer Save.</summary>
    private static ILocator FormSave(IPage page)
        => page.Locator("spark-po-form button[type='submit']", new() { HasTextString = "Save" });

    private static ILocator Attribution(IPage page) => page.Locator("spark-contribution-attribution").First;

    [Fact]
    public async Task A_translator_adds_edits_and_withdraws_a_version_and_a_moderator_reverts_to_it()
    {
        var host = fixture.Host;
        using var author = await host.CreateUserAsync("ui-tr-author");
        using var translator = await host.CreateUserAsync("ui-tr-translator");
        using var other = await host.CreateUserAsync("ui-tr-other");
        var question = await author.Client.AskAsync("Browser translation " + Guid.NewGuid().ToString("N")[..10]);
        var questionId = question.Id!;
        var currentId = $"{questionId}/Translations/nl/Latn";

        var translatorContext = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, BaseURL = host.AppUrl });
        var moderatorContext = await fixture.Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, BaseURL = host.AppUrl });
        try
        {
            var page = await translatorContext.NewPageAsync();
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await BrowserSignIn.SignInAsync(page, host.AppUrl, translator.Email, translator.Password);

            // 1. Add a version. Not the author: the form offers only the translations.
            await page.GotoAsync($"/po/question/{Encoded(questionId)}/edit");
            var add = page.Locator("spark-po-form").GetByRole(AriaRole.Button, new() { Name = "Add" });
            await add.WaitForAsync(new() { Timeout = Timeout });
            (await page.Locator("spark-po-form input#Title").CountAsync()).Should().Be(0, "the question's own title is the author's");
            await add.ClickAsync();
            var editor = RowEditor(page);
            await editor.Locator("input#Language").FillAsync("nl");
            await editor.Locator("input#Script").FillAsync("Latn");
            await editor.Locator("input#Title").FillAsync("Vertaalde titel");
            await editor.Locator("textarea#Body").FillAsync("Eerste regel.\nTweede regel.");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).Last.ClickAsync();
            await SaveFormAsync(page, questionId);

            await Attribution(page).WaitForAsync(new() { Timeout = Timeout });
            (await Attribution(page).InnerTextAsync()).Should().Contain(translator.Email).And.Contain("History (1)");

            // 2. Edit it: still the translator's one version.
            await page.GotoAsync($"/po/question/{Encoded(questionId)}/edit");
            var row = page.Locator("spark-po-form tbody tr").Filter(new() { HasTextString = "Vertaalde titel" });
            await row.Locator("button.btn-outline-secondary").ClickAsync();
            await RowEditor(page).Locator("textarea#Body").FillAsync("Eerste regel.\nTweede regel, verbeterd.");
            await RowEditor(page).GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).Last.ClickAsync();
            await SaveFormAsync(page, questionId);
            (await host.LoadAsync<StoredTranslation>(currentId))!.Body.Should().Be("Eerste regel.\nTweede regel, verbeterd.");
            var translatorsVersion = (await host.LoadAsync<StoredTranslation>(currentId))!.ContributionId!;

            // Someone else writes a newer version of the same language: theirs is shown now.
            await other.Client.SaveTranslationsAsync(questionId, po => po.EditTranslation("nl/Latn", "Andere titel", "Een andere vertaling."));

            // 3. The History link: the contributions of this question, filtered to the slot (chips).
            await page.ReloadAsync();
            await Attribution(page).WaitForAsync(new() { Timeout = Timeout });
            (await Attribution(page).InnerTextAsync()).Should().Contain(other.Email).And.Contain("History (2)");
            await Attribution(page).Locator("a").ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/query/questiontranslationscontributions", StringComparison.Ordinal), new() { Timeout = Timeout });
            await page.Locator(".badge").Filter(new() { HasTextString = "Language: nl" }).WaitForAsync(new() { Timeout = Timeout });
            await page.Locator(".badge").Filter(new() { HasTextString = "Script: Latn" }).WaitForAsync(new() { Timeout = Timeout });
            var historyRows = page.Locator("spark-query-grid tbody tr, tbody tr").Filter(new() { HasTextString = "nl" });
            await historyRows.Nth(1).WaitForAsync(new() { Timeout = Timeout });
            (await historyRows.First.InnerTextAsync()).Should().Contain(other.Email, "newest first");

            // 4. A moderator reverts to the translator's version from its page (the line diff is there too).
            var moderatorPage = await moderatorContext.NewPageAsync();
            moderatorPage.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await BrowserSignIn.SignInAsync(moderatorPage, host.AppUrl, host.AdminEmailAddress, host.AdminPass);
            await moderatorPage.GotoAsync($"/po/questiontranslationscontribution/{Encoded(translatorsVersion)}");
            await moderatorPage.Locator("spark-line-diff").First.WaitForAsync(new() { Timeout = Timeout });
            // The one on screen: bs-priority-nav stamps each action-bar item more than once (the bar,
            // the overflow menu, a measuring copy), and only one of them is visible.
            var revert = moderatorPage.Locator("button.spark-revert-contribution").Filter(new() { Visible = true }).First;
            await revert.WaitForAsync(new() { Timeout = Timeout });
            await revert.ClickAsync();
            await AsyncWaitFor(async () => (await host.LoadAsync<StoredTranslation>(currentId))?.ContributorId == translator.Id);

            // 5. The translator withdraws their version (removes the row): a notice says what is left.
            await page.GotoAsync($"/po/question/{Encoded(questionId)}/edit");
            row = page.Locator("spark-po-form tbody tr").Filter(new() { HasTextString = "Vertaalde titel" });
            await row.Locator("button.btn-outline-danger").ClickAsync();
            await row.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = Timeout });
            await FormSave(page).ClickAsync();
            var toast = page.Locator(".spark-toast-container").Filter(new() { HasTextString = "nl/Latn" });
            await toast.WaitForAsync(new() { Timeout = Timeout });
            (await host.LoadAsync<StoredTranslation>(currentId)).Should().BeNull("the newer version was hidden by the revert, so nothing visible is left");
        }
        finally
        {
            await translatorContext.CloseAsync();
            await moderatorContext.CloseAsync();
        }
    }

    /// <summary>Polls a condition the UI caused; the bound is a failure bound, not an expected duration.</summary>
    private static async Task AsyncWaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within the bound.");
            await Task.Delay(200);
        }
    }
}
