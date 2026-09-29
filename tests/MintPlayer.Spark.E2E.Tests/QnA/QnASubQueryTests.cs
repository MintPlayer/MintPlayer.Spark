using System.Net;
using Microsoft.Playwright;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// #460 M15 — sub-query selection &amp; actions on a question's Answers card: row checkboxes (the entry
/// declares <c>selectionMode: multiple</c>), the select-all box and the "N selected" chip, the bulk
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

        await author.Client.DeletePersistentObjectsAsync(AnswerTypeId, [answers[0], answers[1]],
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

        var delete = () => author.Client.DeletePersistentObjectsAsync(AnswerTypeId, [answers[0], foreign.Id!]);

        (await delete.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the row rule refuses the other user's answer, which refuses the lot like a missing row");
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

            // Select all -> "3 selected"; Duplicate (=1) is disabled with three.
            await card.Locator(".spark-select-all-input").ClickAsync();
            var chip = card.Locator(".spark-selection-chip");
            await chip.WaitForAsync(new() { Timeout = 15_000 });
            (await chip.InnerTextAsync()).Should().Contain("3");
            (await card.Locator("bs-card-header [data-action='DuplicateAnswer']").IsDisabledAsync()).Should().BeTrue();
            (await card.Locator("bs-card-header [data-action='Delete']").IsDisabledAsync()).Should().BeFalse();

            // The chip clears the selection.
            await card.Locator(".spark-selection-clear").ClickAsync();
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
            (await card.Locator("bs-card-header [data-action='Delete']").IsDisabledAsync()).Should().BeTrue("Delete is '>0'");

            // Tick two rows, delete them (the confirm is accepted).
            await rows.Nth(0).Locator("td.checkbox-cell mp-checkbox").ClickAsync();
            await rows.Nth(1).Locator("td.checkbox-cell mp-checkbox").ClickAsync();
            (await chip.InnerTextAsync()).Should().Contain("2");
            await card.Locator("bs-card-header [data-action='Delete']").ClickAsync();
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

    [Fact]
    public async Task New_from_the_Answers_card_creates_an_answer_under_the_question()
    {
        var (author, questionId, _) = await QuestionWithAnswersAsync("new-ui", 0);
        using var _ = author;
        var (context, page) = await OpenQuestionAsync(author, questionId);
        try
        {
            var newButton = page.Locator("spark-query-card bs-card-header [data-action='New']").First;
            await newButton.WaitForAsync(new() { Timeout = 15_000 });
            await newButton.ClickAsync();

            await page.WaitForURLAsync(url => url.Contains("/po/answer/new") && url.Contains("parentId="), new() { Timeout = 15_000 });

            var body = page.Locator("spark-po-form textarea").First;
            await body.WaitForAsync(new() { Timeout = 15_000 });
            await body.FillAsync("Created from the sub-query");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).First.ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/po/answer/") && !url.Contains("/new"), new() { Timeout = 15_000 });

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
