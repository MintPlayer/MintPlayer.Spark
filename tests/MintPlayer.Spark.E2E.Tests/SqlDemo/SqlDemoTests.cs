using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Playwright;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.SqlDemo;

/// <summary>
/// #536 M9: a Spark app on PostgreSQL end to end (<c>apps/SqlDemo</c>), with the features the RavenDB demos use. The
/// book list is <c>Books_WithAuthor</c>, a SQL view the app's migration created (D32); History stamps and keeps
/// revisions in tables (S8); registration mails go through Messaging's table and the change feed (D39).
/// </summary>
[Collection(SqlDemoE2ECollection.Name)]
public partial class SqlDemoTests
{
    private const int Timeout = 15_000;

    private readonly SqlDemoE2ECollectionFixture fixture;
    public SqlDemoTests(SqlDemoE2ECollectionFixture fixture) => this.fixture = fixture;

    private SqlDemoTestHost Host => fixture.Host;

    [Fact]
    public async Task Anyone_reads_the_book_list_from_the_view_with_the_author_joined_in()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var admin = await AdminAsync();
        var author = await CreateAuthorAsync(admin, $"Ursula {run}");
        var book = await CreateBookAsync(admin, $"The Dispossessed {run}", 1974, author.Id);

        using var anonymous = Host.NewClient();
        var result = await anonymous.ExecuteQueryAsync(SqlDemoTestHost.GetBooksQueryId, take: 100);

        var row = result.Items.SingleOrDefault(i => i.Id == book.Id);
        row.Should().NotBeNull($"the view lists the new book {book.Id}; rows: {string.Join(", ", result.Items.Select(i => i.Id))}\n{Host.RecentLog(40)}");
        Cell(row!, "Title").Should().Be($"The Dispossessed {run}");
        Cell(row!, "AuthorName").Should().Be($"Ursula {run}", "Books_WithAuthor joins the author's name in");
    }

    [Fact]
    public async Task A_book_without_an_author_is_listed_with_an_empty_author_name()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var admin = await AdminAsync();
        var book = await CreateBookAsync(admin, $"Anonymous verses {run}", 1850, authorId: null);

        var result = await admin.ExecuteQueryAsync(SqlDemoTestHost.GetBooksQueryId, take: 100);

        var row = result.Items.SingleOrDefault(i => i.Id == book.Id);
        row.Should().NotBeNull("the view's join is an outer one: a book without an author is still a row");
        Cell(row!, "AuthorName").Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task History_keeps_the_revisions_of_a_book_with_the_admin_as_their_author()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var admin = await AdminAsync();
        var book = await CreateBookAsync(admin, $"Draft title {run}", 1968, authorId: null);
        await admin.EditAsync(SqlDemoTestHost.BookTypeId, book.Id!, "Title", $"A Wizard of Earthsea {run}");

        var request = new { objectTypeId = SqlDemoTestHost.BookTypeId.ToString(), id = book.Id };
        var revisions = (await admin.PostJsonAsync("/spark/po/revisions", request)).Result().EnumerateArray().ToList();
        revisions.Count.Should().BeGreaterThanOrEqualTo(2, "Book.json enables revisions: the create and the edit");
        revisions[^1].GetProperty("userId").GetString().Should().Be(Host.AdminId);

        var changeVector = revisions[^1].GetProperty("changeVector").GetString()!;
        var oldest = (await admin.PostJsonAsync("/spark/po/revision", new { objectTypeId = SqlDemoTestHost.BookTypeId.ToString(), id = book.Id, changeVector })).Result();
        AttributeValue(oldest, "Title").Should().Be($"Draft title {run}");
    }

    [Fact]
    public async Task Registration_mails_a_confirmation_link_and_the_confirmed_account_signs_in()
    {
        var email = $"reader-{Guid.NewGuid():N}@sqldemo.example";
        var password = $"Aa1!{Guid.NewGuid():N}";
        using var client = Host.NewClient();
        await client.RegisterAsync(email, password, email[..email.IndexOf('@')]);

        // Queued as a message row, picked up through the change feed, written as an .eml by MailManager.
        var mail = await Host.WaitForMailAsync(email, "Confirm your email");
        var text = HttpUtility.HtmlDecode(mail.TextBody ?? mail.HtmlBody ?? string.Empty);
        var link = ConfirmLink().Match(text);
        link.Success.Should().BeTrue($"the mail carries a confirm-email link: {text}");

        var query = HttpUtility.ParseQueryString(new Uri(link.Value).Query);
        var (confirmed, body) = await client.PostAsync("/spark/auth/confirm-email", new { userId = query["userId"], code = query["code"] });
        confirmed.Should().BeOneOf([200, 204], $"the mailed link confirms the account (answer: {body})");

        await client.LoginAsync(email, password);
        (await client.GetCurrentUserAsync()).IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task The_books_page_shows_the_views_rows()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using (var admin = await AdminAsync())
        {
            var author = await CreateAuthorAsync(admin, $"Octavia {run}");
            await CreateBookAsync(admin, $"Kindred {run}", 1979, author.Id);
        }

        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = Host.AppUrl,
        });
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"/query/{SqlDemoTestHost.GetBooksQueryId}");

            var row = page.Locator("tbody tr", new() { HasText = $"Kindred {run}" });
            await row.WaitForAsync(new() { Timeout = Timeout });
            (await row.InnerTextAsync()).Should().Contain($"Octavia {run}", "the grid shows the view's joined column");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private async Task<SparkClient> AdminAsync()
    {
        var client = Host.NewClient();
        await client.LoginAsync(Host.AdminEmailAddress, Host.AdminPass);
        return client;
    }

    /// <summary>The create, with the app's log in the failure: a 500's body says nothing about its cause.</summary>
    private async Task<PersistentObject> CreateAsync(SparkClient client, PersistentObject po)
    {
        try
        {
            return await client.CreatePersistentObjectAsync(po);
        }
        catch (SparkClientException ex)
        {
            throw new InvalidOperationException($"Creating a {po.Name} failed: {ex.Message}\n--- SqlDemo log ---\n{Host.RecentLog(80)}", ex);
        }
    }

    private async Task<PersistentObject> CreateAuthorAsync(SparkClient client, string name)
    {
        var created = await CreateAsync(client, new PersistentObject
        {
            Name = "Author",
            ObjectTypeId = SqlDemoTestHost.AuthorTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Name", Value = name, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Country", Value = "United States", IsValueChanged = true },
            ],
        });
        created.Id.Should().NotBeNullOrEmpty($"Author '{name}' is created");
        return created;
    }

    private async Task<PersistentObject> CreateBookAsync(SparkClient client, string title, int year, string? authorId)
    {
        var created = await CreateAsync(client, new PersistentObject
        {
            Name = "Book",
            ObjectTypeId = SqlDemoTestHost.BookTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Title", Value = title, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Year", Value = year, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Author", Value = authorId, DataType = "Reference", IsValueChanged = true },
            ],
        });
        created.Id.Should().NotBeNullOrEmpty($"Book '{title}' is created");
        return created;
    }

    private static string? Cell(QueryResultItem row, string column)
        => row.Values.SingleOrDefault(v => v.Key == column)?.Value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            var other => other.ToString(),
        };

    private static string? AttributeValue(JsonElement po, string name)
        => po.GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name)
            .GetProperty("value").GetString();

    // Parentheses excluded: the text part renders a link as "label (url)" (QnAAccountTests).
    [GeneratedRegex(@"https?://[^\s""'<>()]+/confirm-email\?[^\s""'<>()]+")]
    private static partial Regex ConfirmLink();
}
