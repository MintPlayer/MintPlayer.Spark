using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Visibility;

/// <summary>
/// #264, bug 2 (PRD §2.3/§8): HR's <c>Person.LastName</c> is required, but <c>isVisible: false</c> left it off the
/// create form, so every create from the SPA was answered 400 "Last Name is required." with nothing on the page.
/// </summary>
/// <remarks>
/// Driven through the browser on purpose: the server half (a posted LastName is kept) is covered by the unit
/// suite, and only the real client shows which attributes the create form draws.
/// </remarks>
[Collection(HRE2ECollection.Name)]
public class HRCreatePersonTests
{
    private readonly HRE2ECollectionFixture _fixture;
    public HRCreatePersonTests(HRE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_create_form_shows_LastName_and_a_person_can_be_created()
    {
        var host = _fixture.Host;
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
        });
        try
        {
            var page = await context.NewPageAsync();
            await BrowserSignIn.SignInAsync(page, host.AppUrl, host.AdminEmailAddress, host.AdminPass);

            await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/new");

            var firstName = page.Locator("input#FirstName");
            await firstName.WaitForAsync(new() { Timeout = 15_000 });

            var lastName = page.Locator("input#LastName");
            (await lastName.CountAsync()).Should().Be(1,
                "LastName is required, so the create form must draw it; without it no person can be created");

            var surname = $"Doe{Guid.NewGuid():N}"[..11];
            await firstName.FillAsync("Jane");
            await lastName.FillAsync(surname);
            await page.Locator("button[type='submit']").ClickAsync();

            // Leaving /new is the save succeeding (the create component navigates to the detail page).
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (page.Url.EndsWith("/new", StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline)
                {
                    var body = await page.Locator("body").InnerTextAsync();
                    false.Should().BeTrue($"the create should succeed and leave /new; the page shows:\n{body}\n--- HR log tail ---\n{host.RecentLog()}");
                }
                await Task.Delay(100);
            }

            var id = Uri.UnescapeDataString(page.Url.TrimEnd('/').Split('/')[^1]);
            var stored = await host.LoadAsync<StoredPerson>(id);
            stored.Should().NotBeNull($"the person '{id}' must be stored");
            stored!.FirstName.Should().Be("Jane");
            stored.LastName.Should().Be(surname, "the value typed into the form is the value stored");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>The stored shape this test reads; HR's entity assembly is not referenced by the E2E project.</summary>
    private sealed class StoredPerson
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
    }
}
