using System.Net;
using Microsoft.Playwright;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// #460 M15 — sub-query selection &amp; actions on a question's Answers card: row checkboxes (the entry
/// declares <c>selectionMode: multiple</c>), the "N selected" chip and the datatable's deselect-all
/// (there is no select-all), the bulk
/// Delete (soft, all or nothing), the <c>=1</c> Duplicate enabled from the selection and offered in
/// the row menu, and New carrying the question to the create page, where the base <c>OnNewAsync</c>
/// fills <c>Answer.QuestionId</c>.
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnASubQueryTests
{
    private readonly QnAE2ECollectionFixture fixture;
    private QnATestHost Host => fixture.Host;

    public QnASubQueryTests(QnAE2ECollectionFixture fixture) => this.fixture = fixture;

    private async Task<(QnAUser Author, string QuestionId, string[] AnswerIds)> QuestionWithAnswersAsync(string name, int answers)
    {
        var author = await Host.CreateUserAsync(name);
        var question = await author.Client.AskAsync($"Sub-query {name} " + Guid.NewGuid().ToString("N")[..10]);
        var ids = new List<string>();
        for (var i = 0; i < answers; i++)
            ids.Add((await author.Client.AnswerAsync(question.Id!, $"Answer {i} of {name}")).Id!);
        await Host.WaitForIndexingAsync();
        return (author, question.Id!, [.. ids]);
    }

    private async Task<string[]> LiveAnswerIdsAsync(SparkClient client, string questionId)
    {
        await Host.WaitForIndexingAsync();
        var result = await client.ExecuteQueryAsync(QuestionAnswersQueryId, take: 100, parentId: questionId, parentType: "Question");
        return [.. result.Items.Select(i => i.Id)];
    }

    // ---------- over the API ----------

    [Fact]
    public async Task A_bulk_delete_from_the_sub_query_soft_deletes_every_selected_answer()
    {
        var (author, questionId, answers) = await QuestionWithAnswersAsync("bulk-api", 3);
        using var _ = author;

        await author.Client.DeletePersistentObjectsAsync(AnswerTypeId, await author.Client.AsListedAsync(AnswerTypeId, answers[0], answers[1]),
            queryId: QuestionAnswersQueryId.ToString(), parentId: questionId, parentType: "Question");

        (await Host.LoadAsync<StoredPost>(answers[0]))!.IsDeleted.Should().BeTrue("QnA's answers are soft-deletable");
        (await Host.LoadAsync<StoredPost>(answers[1]))!.IsDeleted.Should().BeTrue();
        (await Host.LoadAsync<StoredPost>(answers[2]))!.IsDeleted.Should().BeFalse();
        (await LiveAnswerIdsAsync(author.Client, questionId)).Should().Equal(answers[2]);
    }

    [Fact]
    public async Task One_answer_the_caller_may_not_delete_refuses_the_whole_selection()
    {
        var (author, questionId, answers) = await QuestionWithAnswersAsync("bulk-mixed", 1);
        using var _ = author;
        using var other = await Host.CreateUserAsync("bulk-other");
        var foreign = await other.Client.AnswerAsync(questionId, "Not the author's to delete");

        var listed = await author.Client.AsListedAsync(AnswerTypeId, answers[0], foreign.Id!);
        var delete = () => author.Client.DeletePersistentObjectsAsync(AnswerTypeId, listed,
            queryId: QuestionAnswersQueryId.ToString(), parentId: questionId, parentType: "Question");

        // #467 D18: the other user's answer is readable, so the refusal names it rather than
        // pretending it is missing (only an unreadable row counts as missing, D11).
        (await delete.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the Delete row rule refuses the other user's answer, which refuses the lot");
        (await Host.LoadAsync<StoredPost>(answers[0]))!.IsDeleted.Should().BeFalse("all or nothing");
        (await Host.LoadAsync<StoredPost>(foreign.Id!))!.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task New_from_the_sub_query_fills_the_question_through_the_base_hook()
    {
        var (author, questionId, _) = await QuestionWithAnswersAsync("new-api", 0);
        using var _ = author;

        var blank = await author.Client.NewPersistentObjectFromSubQueryAsync("answer", "Question", questionId, "question-answers");

        blank["QuestionId"].Value?.ToString().Should().Be(questionId);
    }

    [Fact]
    public async Task Duplicate_needs_exactly_one_answer()
    {
        var (author, questionId, answers) = await QuestionWithAnswersAsync("dup-api", 2);
        using var _ = author;

        var two = () => author.Client.ExecuteActionAsync(AnswerTypeId, "DuplicateAnswer", null, answers,
            parentId: questionId, parentType: "Question", queryId: QuestionAnswersQueryId.ToString());
        (await two.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await author.Client.ExecuteActionAsync(AnswerTypeId, "DuplicateAnswer", null, [answers[0]],
            parentId: questionId, parentType: "Question", queryId: QuestionAnswersQueryId.ToString());
        (await LiveAnswerIdsAsync(author.Client, questionId)).Should().HaveCount(3);
    }

    // ---------- in the browser ----------

    private async Task<(IBrowserContext Context, IPage Page)> OpenQuestionAsync(QnAUser user, string questionId)
    {
        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BaseURL = Host.AppUrl });
        var page = await context.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await BrowserSignIn.SignInAsync(page, Host.AppUrl, user.Email, user.Password);
        await page.GotoAsync($"/po/question/{Uri.EscapeDataString(questionId)}");
        return (context, page);
    }

    /// <summary>
    /// The card header's button for an action — the one on screen. <c>bs-priority-nav</c> stamps each
    /// item three times (an inert measuring copy, the strip, and the "…" overflow list), so a bare
    /// <c>[data-action]</c> selector matches three elements and trips strict mode. Only one is ever
    /// visible: the strip's while it fits, the overflow list's while "…" is open.
    /// </summary>
    private static ILocator CardAction(ILocator card, string name) =>
        card.Locator($"bs-card-header [data-action='{name}']").Filter(new() { Visible = true });

    /// <summary>
    /// Waits for the "N selected" chip to show <paramref name="count"/>. A read straight after the
    /// click raced the selection's change detection ("1 selected" right after the second tick).
    /// </summary>
    private static Task WaitForChipAsync(ILocator chip, int count) =>
        chip.Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex($@"\b{count}\b") })
            .WaitForAsync(new() { Timeout = 15_000 });

    /// <summary>Ticks the checkbox of the first <paramref name="count"/> rows (there is no select-all).</summary>
    private static async Task TickAsync(ILocator rows, int count)
    {
        for (var i = 0; i < count; i++)
            await rows.Nth(i).Locator("td.checkbox-cell mp-checkbox").ClickAsync();
    }

    /// <summary>
    /// Clears the selection with the datatable's header checkbox — its built-in deselect-all, shown
    /// only while a row is selected.
    /// </summary>
    private static Task DeselectAllAsync(ILocator card) =>
        card.Locator("thead th.checkbox-cell mp-checkbox").ClickAsync();

    /// <summary>
    /// Polls <see cref="IPage.Url"/> rather than using <c>WaitForURLAsync</c>, which waits for a
    /// navigation event: the Angular router's same-document navigations were intermittently not seen
    /// under load (the URL had changed, the wait still timed out), with Load and Commit alike.
    /// </summary>
    private static async Task WaitForUrlAsync(IPage page, Func<string, bool> matches, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!matches(page.Url))
        {
            if (DateTime.UtcNow > deadline)
                matches(page.Url).Should().BeTrue($"{because}, but the page is at {page.Url}");
            await Task.Delay(100);
        }
    }

    private static async Task WaitForRowCountAsync(ILocator rows, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await rows.CountAsync() != expected)
        {
            if (DateTime.UtcNow > deadline)
                (await rows.CountAsync()).Should().Be(expected, "the grid should show the expected rows");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task The_Answers_card_selects_rows_and_deletes_a_selection()
    {
        var (author, questionId, answers) = await QuestionWithAnswersAsync("bulk-ui", 3);
        using var _ = author;
        var (context, page) = await OpenQuestionAsync(author, questionId);
        try
        {
            var card = page.Locator("spark-query-card").First;
            var rows = card.Locator("tbody tr").Filter(new() { HasTextString = "of bulk-ui" });
            await rows.First.WaitForAsync(new() { Timeout = 15_000 });
            await WaitForRowCountAsync(rows, 3);

            // Checkboxes, because the entry declares selectionMode: multiple.
            (await card.Locator("td.checkbox-cell mp-checkbox").CountAsync()).Should().Be(3);

            // The header offers New, Delete and the =1 Duplicate — each exactly once on screen.
            foreach (var action in new[] { "New", "Delete", "DuplicateAnswer" })
                (await CardAction(card, action).CountAsync()).Should().Be(1, $"'{action}' is shown once in the card header");

            // No select-all (owner decision): with lazy or virtual rows it could only tick the loaded ones.
            (await card.Locator(".spark-select-all").CountAsync()).Should().Be(0);

            // Tick all three -> "3 selected"; Duplicate (=1) is disabled with three.
            await TickAsync(rows, 3);
            var chip = card.Locator(".spark-selection-chip");
            await chip.WaitForAsync(new() { Timeout = 15_000 });
            await WaitForChipAsync(chip, 3);
            (await CardAction(card, "DuplicateAnswer").IsDisabledAsync()).Should().BeTrue();
            (await CardAction(card, "Delete").IsDisabledAsync()).Should().BeFalse();

            // The datatable's header checkbox is the deselect-all.
            await DeselectAllAsync(card);
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
            (await CardAction(card, "Delete").IsDisabledAsync()).Should().BeTrue("Delete is '>0'");

            // Tick two rows, delete them (the confirm is accepted).
            await rows.Nth(0).Locator("td.checkbox-cell mp-checkbox").ClickAsync();
            await rows.Nth(1).Locator("td.checkbox-cell mp-checkbox").ClickAsync();
            await WaitForChipAsync(chip, 2);
            await CardAction(card, "Delete").ClickAsync();
            await WaitForRowCountAsync(rows, 1);

            var remaining = await LiveAnswerIdsAsync(author.Client, questionId);
            remaining.Should().HaveCount(1);
            foreach (var deleted in answers.Except(remaining))
                (await Host.LoadAsync<StoredPost>(deleted))!.IsDeleted.Should().BeTrue("the bulk delete is soft");

            // The row menu runs Duplicate on that row alone.
            await rows.First.Locator(".spark-row-menu-toggle").ClickAsync();
            await page.Locator(".spark-row-action[data-action='DuplicateAnswer']").ClickAsync();
            await WaitForRowCountAsync(rows, 2);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>
    /// The owner's D17 addendum: a search box in the card header, next to the actions. It narrows the
    /// Answers rows on the server (search + parent), and a new term clears the selection.
    /// </summary>
    [Fact]
    public async Task Searching_the_Answers_card_narrows_its_rows_and_clears_the_selection()
    {
        var (author, questionId, _) = await QuestionWithAnswersAsync("search-ui", 2);
        using var _ = author;
        await author.Client.AnswerAsync(questionId, "Zebra crossing of search-ui");
        await Host.WaitForIndexingAsync();
        var (context, page) = await OpenQuestionAsync(author, questionId);
        try
        {
            var card = page.Locator("spark-query-card").First;
            var rows = card.Locator("tbody tr").Filter(new() { HasTextString = "of search-ui" });
            await rows.First.WaitForAsync(new() { Timeout = 15_000 });
            await WaitForRowCountAsync(rows, 3);

            await TickAsync(rows, 3);
            var chip = card.Locator(".spark-selection-chip");
            await chip.WaitForAsync(new() { Timeout = 15_000 });

            var search = card.Locator("bs-card-header spark-search-box input");
            await search.FillAsync("zebra");
            await WaitForRowCountAsync(rows, 1);
            (await rows.First.InnerTextAsync()).Should().Contain("Zebra crossing");
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });

            // The clear button brings every answer back.
            await card.Locator("bs-card-header .spark-search-clear").ClickAsync();
            await WaitForRowCountAsync(rows, 3);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task New_from_the_Answers_card_creates_an_answer_under_the_question()
    {
        var (author, questionId, _) = await QuestionWithAnswersAsync("new-ui", 0);
        using var _ = author;
        var (context, page) = await OpenQuestionAsync(author, questionId);
        try
        {
            var newButton = CardAction(page.Locator("spark-query-card").First, "New");
            await newButton.WaitForAsync(new() { Timeout = 15_000 });
            // The detail page and its card are still loading (entity types, the sub-query, its
            // actions) when the button first appears; a click during that settling was lost once.
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await newButton.ClickAsync();

            await WaitForUrlAsync(page, url => url.Contains("/po/answer/new") && url.Contains("parentId="),
                "New from the card should open the create page carrying the parent");

            var body = page.Locator("spark-po-form textarea").First;
            await body.WaitForAsync(new() { Timeout = 15_000 });
            await body.FillAsync("Created from the sub-query");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).First.ClickAsync();
            await WaitForUrlAsync(page, url => url.Contains("/po/answer/") && !url.Contains("/new"),
                "saving should open the created answer");

            var created = await LiveAnswerIdsAsync(author.Client, questionId);
            created.Should().HaveCount(1, "the question was filled in by the base OnNewAsync, not by the user");
            (await Host.LoadAsync<StoredPost>(created[0]))!.QuestionId.Should().Be(questionId);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
