using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Tests.IdentityProvider;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 (<c>docs/apps_webhooks_PRD.md</c> D29, D30): what <c>/spark/apps/apps</c> and <c>/spark/apps/installations</c>
/// refuse, the owner limits, and how an App's webhook, keys and installations follow its changes. A refusal is a 400 with
/// the reason in <c>error</c>; anything the caller does not manage is a 404, whether or not it exists.
/// </summary>
public class SparkAppManagementTests : AppsTestHost
{
    protected override void ConfigureApps(SparkAppsOptions options)
    {
        options.MaxAppsPerOwner = 2;
        options.MaxKeysPerApp = 1;
        options.MaxInstallationsPerOwner = 1;
    }

    protected override void ConfigureAppsBuilder(AppsBuilder apps) => apps.EnableApps().MapPermission("orders:read", "OrderReaders");

    protected override void ConfigureSpark(ISparkBuilder spark) => spark.AddIdentityProvider(o =>
    {
        o.Issuer = "https://idp.example.com";
        o.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
    });

    private static Dictionary<string, object?> AppBody(string ownerKey, string slug, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["ownerKind"] = WebhookOwner.UserKind,
            ["ownerKey"] = ownerKey,
            ["slug"] = slug,
            ["name"] = "Shipping bot",
            ["permissions"] = new[] { "orders:read" },
            ["events"] = new[] { "order.shipped" },
            ["public"] = true,
        };
        if (extra is not null)
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                body[property.Name] = property.Value;
        return body;
    }

    private async Task<JsonElement> CreateAppAsync(string userId, string slug, object? extra = null)
    {
        var (status, body) = await SendAsync("/spark/apps/apps", AppBody(userId, slug, extra), userId);
        status.Should().Be(HttpStatusCode.OK, "the developer registers the App: " + body);
        return body;
    }

    private async Task<string> InstallAsync(string slug, string userId)
    {
        var (status, body) = await SendAsync("/spark/apps/installations", new { slug, targetKind = WebhookOwner.UserKind, targetKey = userId }, userId);
        status.Should().Be(HttpStatusCode.OK, "the user installs a public App on their own account: " + body);
        return body.GetProperty("installation").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_new_App_is_refused_for_each_invalid_field_and_nothing_is_stored()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        (string What, Dictionary<string, object?> Body, string Error)[] cases =
        [
            ("no owner", AppBody(ann.Id!, "bot", new { ownerKind = (string?)null }), "ownerKind and ownerKey are required."),
            ("an upper-case slug", AppBody(ann.Id!, "Bot"), "The slug is 1 to 40 lowercase letters, digits and dashes, starting and ending with a letter or digit."),
            ("a slug ending in a dash", AppBody(ann.Id!, "bot-"), "The slug is 1 to 40 lowercase letters, digits and dashes, starting and ending with a letter or digit."),
            ("a blank name", AppBody(ann.Id!, "bot", new { name = "   " }), "The name is 1 to 80 characters."),
            ("a long name", AppBody(ann.Id!, "bot", new { name = new string('n', 81) }), "The name is 1 to 80 characters."),
            ("a long description", AppBody(ann.Id!, "bot", new { description = new string('d', 1001) }), "The description is at most 1000 characters."),
            ("a relative homepage", AppBody(ann.Id!, "bot", new { homepageUrl = "/home" }), "The homepage URL must be an absolute http(s) URL."),
            ("an ftp setup URL", AppBody(ann.Id!, "bot", new { setupUrl = "ftp://app.example.com/setup" }), "The setup URL must be an absolute http(s) URL."),
            ("a plain-http webhook", AppBody(ann.Id!, "bot", new { webhookUrl = "http://hooks.example.com/in" }), "The URL must use https."),
            ("a malformed permission", AppBody(ann.Id!, "bot", new { permissions = new[] { "orders" } }), "'orders' is not a permission; use resource:read or resource:write."),
            ("an unknown event", AppBody(ann.Id!, "bot", new { events = new[] { "nope.happened" } }), "Unknown event: nope.happened"),
            ("every event", AppBody(ann.Id!, "bot", new { events = new[] { "*" } }), "Unknown event: *"),
            ("an event without its permission", AppBody(ann.Id!, "bot", new { permissions = Array.Empty<string>() }), "The event 'order.shipped' needs the permission 'orders:read'."),
            ("51 permissions", AppBody(ann.Id!, "bot", new { permissions = Enumerable.Range(0, 51).Select(i => $"r{i}:read").ToArray() }), "Too many permissions or events."),
        ];

        foreach (var (what, body, error) in cases)
        {
            var (status, answer) = await SendAsync("/spark/apps/apps", body, ann.Id);
            status.Should().Be(HttpStatusCode.BadRequest, $"{what} is refused");
            answer.GetProperty("error").GetString().Should().Be(error, $"the refusal of {what} says why");
        }
        (await SendAsync("/spark/apps/apps", AppBody(ann.Id!, "bot"), bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob cannot register an App on ann's account");

        var (listed, list) = await GetAsync($"/spark/apps/apps?ownerKind={WebhookOwner.UserKind}&ownerKey={Uri.EscapeDataString(ann.Id!)}", ann.Id);
        listed.Should().Be(HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(0, "no refused App was stored");
    }

    [Fact]
    public async Task Listing_and_reading_Apps_answer_only_what_the_caller_may_see()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "first");
        await CreateAppAsync(ann.Id!, "second", new { @public = false });

        (await GetAsync("/spark/apps/apps", ann.Id)).Status.Should().Be(HttpStatusCode.BadRequest, "the owner is required");
        (await GetAsync($"/spark/apps/apps?ownerKind={WebhookOwner.UserKind}&ownerKey={Uri.EscapeDataString(ann.Id!)}", bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob does not manage ann's Apps");
        var (_, mine) = await GetAsync($"/spark/apps/apps?ownerKind={WebhookOwner.UserKind}&ownerKey={Uri.EscapeDataString(ann.Id!)}", ann.Id);
        mine.EnumerateArray().Select(a => a.GetProperty("slug").GetString()).Should().Equal(["first", "second"]);
        var (_, bobs) = await GetAsync($"/spark/apps/apps?ownerKind={WebhookOwner.UserKind}&ownerKey={Uri.EscapeDataString(bob.Id!)}", bob.Id);
        bobs.GetArrayLength().Should().Be(0, "bob has no Apps yet");

        (await GetAsync("/spark/apps/apps/app?slug=Not_A_Slug", ann.Id)).Status.Should().Be(HttpStatusCode.NotFound, "a malformed slug names nothing");
        (await GetAsync("/spark/apps/apps/app?slug=missing", ann.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/spark/apps/apps/app", ann.Id)).Status.Should().Be(HttpStatusCode.NotFound, "no slug names nothing");
    }

    [Fact]
    public async Task An_owner_has_at_most_MaxAppsPerOwner_Apps_and_a_slug_is_taken_across_owners()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "first");
        await CreateAppAsync(ann.Id!, "second");

        var (status, body) = await SendAsync("/spark/apps/apps", AppBody(ann.Id!, "third"), ann.Id);
        status.Should().Be(HttpStatusCode.BadRequest, "the third App exceeds the limit of 2");
        body.GetProperty("error").GetString().Should().Be("An owner has at most 2 Apps.");

        var (taken, conflict) = await SendAsync("/spark/apps/apps", AppBody(bob.Id!, "first"), bob.Id);
        taken.Should().Be(HttpStatusCode.Conflict, "slugs are global, not per owner");
        conflict.GetProperty("error").GetString().Should().Be("The slug 'first' is taken.");
    }

    [Fact]
    public async Task An_edit_is_refused_for_each_invalid_field_and_changes_nothing()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "bot", new { homepageUrl = "https://bot.example.com" });

        (object Body, string Error)[] refused =
        [
            (new { slug = "bot", name = new string('n', 81) }, "The name is 1 to 80 characters."),
            (new { slug = "bot", description = new string('d', 1001) }, "The description is at most 1000 characters."),
            (new { slug = "bot", homepageUrl = "not a url" }, "The homepage URL must be an absolute http(s) URL."),
            (new { slug = "bot", setupUrl = "https://user:pw@app.example.com/setup" }, "The setup URL must be an absolute http(s) URL."),
            (new { slug = "bot", webhookUrl = "https://user:pw@hooks.example.com/in" }, "The URL must not contain credentials."),
            (new { slug = "bot", permissions = Array.Empty<string>() }, "The event 'order.shipped' needs the permission 'orders:read'."),
            (new { slug = "bot", events = new[] { "nope.happened" } }, "Unknown event: nope.happened"),
        ];
        foreach (var (body, error) in refused)
        {
            var (status, answer) = await SendAsync("/spark/apps/apps/update", body, ann.Id);
            status.Should().Be(HttpStatusCode.BadRequest);
            answer.GetProperty("error").GetString().Should().Be(error);
        }
        (await SendAsync("/spark/apps/apps/update", new { slug = "bot", name = "Taken over" }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob does not manage ann's App");
        (await SendAsync("/spark/apps/apps/update", new { slug = "BOT", name = "x" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a malformed slug names nothing");

        using var session = Store.OpenAsyncSession();
        var app = (await session.LoadAsync<SparkApp>(SparkApp.IdFor("bot")))!;
        app.Name.Should().Be("Shipping bot", "no refused edit was stored");
        app.HomepageUrl.Should().Be("https://bot.example.com");
        app.Permissions.Should().Equal(["orders:read"]);
    }

    [Fact]
    public async Task Clearing_the_webhook_URL_removes_the_webhook_and_a_new_URL_adds_one_with_a_new_secret()
    {
        var ann = await SeedUserAsync("ann");
        var created = await CreateAppAsync(ann.Id!, "bot", new { webhookUrl = ReceiverUrl, description = "Ships" });
        var firstHook = created.GetProperty("app").GetProperty("webhookEndpointId").GetString()!;
        await UntilSettledAsync(firstHook, 1);

        var (cleared, clearedBody) = await SendAsync("/spark/apps/apps/update", new { slug = "bot", webhookUrl = "", description = "" }, ann.Id);

        cleared.Should().Be(HttpStatusCode.OK, clearedBody.ToString());
        var clearedApp = clearedBody.GetProperty("app");
        clearedApp.GetProperty("webhookEndpointId").ValueKind.Should().Be(JsonValueKind.Null, "the App has no webhook any more");
        clearedApp.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null, "an empty description clears it");
        clearedBody.GetProperty("webhookSecret").ValueKind.Should().Be(JsonValueKind.Null);
        using (var session = Store.OpenAsyncSession())
            (await session.Advanced.ExistsAsync(WebhookEndpoint.IdPrefix + firstHook)).Should().BeFalse("the App's webhook is deleted with its URL");

        var (added, addedBody) = await SendAsync("/spark/apps/apps/update", new { slug = "bot", webhookUrl = "  https://hooks.example.com/again  " }, ann.Id);

        added.Should().Be(HttpStatusCode.OK, addedBody.ToString());
        addedBody.GetProperty("webhookSecret").GetString().Should().StartWith("whsec_", "a new webhook shows its secret once");
        var secondHook = addedBody.GetProperty("app").GetProperty("webhookEndpointId").GetString()!;
        secondHook.Should().NotBe(firstHook, "a new webhook, not the old one back");
        addedBody.GetProperty("app").GetProperty("webhookUrl").GetString().Should().Be("https://hooks.example.com/again");
        var hook = await EndpointAsync(secondHook);
        hook.AppSlug.Should().Be("bot");
        hook.Events.Should().Equal(["order.shipped"]);
        await UntilSettledAsync(secondHook, 1);
        Receiver.Received.Select(r => r.Url).Should().Contain("https://hooks.example.com/again", "the new webhook gets a ping");
    }

    [Fact]
    public async Task An_App_holds_at_most_MaxKeysPerApp_keys_and_only_its_managers_change_them()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "bot");

        (await SendAsync("/spark/apps/apps/keys/generate", new { slug = "bot" }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob does not manage ann's App");
        var (first, key) = await SendAsync("/spark/apps/apps/keys/generate", new { slug = "bot" }, ann.Id);
        first.Should().Be(HttpStatusCode.OK, key.ToString());
        var keyId = key.GetProperty("key").GetProperty("keyId").GetString()!;

        var (second, refusal) = await SendAsync("/spark/apps/apps/keys/generate", new { slug = "bot" }, ann.Id);
        second.Should().Be(HttpStatusCode.BadRequest, "the second key exceeds the limit of 1");
        refusal.GetProperty("error").GetString().Should().Be("An App holds at most 1 keys; delete one first.");

        (await SendAsync("/spark/apps/apps/keys/delete", new { slug = "bot", keyId = "not-a-key" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a malformed key id names nothing");
        (await SendAsync("/spark/apps/apps/keys/delete", new { slug = "bot", keyId = new string('0', 16) }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a well-formed id of a key the App does not hold");
        (await SendAsync("/spark/apps/apps/keys/delete", new { slug = "bot", keyId }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob cannot delete ann's key");

        var (deleted, view) = await SendAsync("/spark/apps/apps/keys/delete", new { slug = "bot", keyId }, ann.Id);
        deleted.Should().Be(HttpStatusCode.OK, view.ToString());
        view.GetProperty("keys").GetArrayLength().Should().Be(0);
        (await SendAsync("/spark/apps/apps/keys/generate", new { slug = "bot" }, ann.Id)).Status
            .Should().Be(HttpStatusCode.OK, "with the old key deleted, a new one fits");
    }

    [Fact]
    public async Task The_install_endpoints_refuse_what_names_nothing_or_what_the_caller_does_not_manage()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "bot");
        await CreateAppAsync(ann.Id!, "other");

        (await GetAsync("/spark/apps/installations/new?slug=Bad_Slug", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/spark/apps/installations/new?slug=missing", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);

        var (missing, missingBody) = await SendAsync("/spark/apps/installations", new { slug = "bot" }, bob.Id);
        missing.Should().Be(HttpStatusCode.BadRequest);
        missingBody.GetProperty("error").GetString().Should().Be("slug, targetKind and targetKey are required.");
        (await SendAsync("/spark/apps/installations", new { slug = "bot", targetKind = WebhookOwner.UserKind, targetKey = ann.Id }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "bob cannot install on ann's account");
        (await SendAsync("/spark/apps/installations", new { slug = "missing", targetKind = WebhookOwner.UserKind, targetKey = bob.Id }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound);

        await InstallAsync("bot", bob.Id!);
        var (overLimit, overLimitBody) = await SendAsync("/spark/apps/installations", new { slug = "other", targetKind = WebhookOwner.UserKind, targetKey = bob.Id }, bob.Id);
        overLimit.Should().Be(HttpStatusCode.BadRequest, "bob's second installation exceeds the limit of 1");
        overLimitBody.GetProperty("error").GetString().Should().Be("An owner has at most 1 Apps installed.");

        var (noTarget, noTargetBody) = await GetAsync("/spark/apps/installations", bob.Id);
        noTarget.Should().Be(HttpStatusCode.BadRequest);
        noTargetBody.GetProperty("error").GetString().Should().Be("targetKind and targetKey are required.");
        (await GetAsync($"/spark/apps/installations?targetKind={WebhookOwner.UserKind}&targetKey={Uri.EscapeDataString(bob.Id!)}", ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "ann does not manage bob's account");
        (await GetAsync("/spark/apps/apps/installations?slug=bot", bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "only the App's managers list its installations");
        (await GetAsync("/spark/apps/apps/installations?slug=Bad_Slug", ann.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        var (listed, installations) = await GetAsync("/spark/apps/apps/installations?slug=bot", ann.Id);
        listed.Should().Be(HttpStatusCode.OK);
        installations.GetArrayLength().Should().Be(1);

        foreach (var path in new[] { "suspend", "unsuspend", "accept", "delete" })
            (await SendAsync($"/spark/apps/installations/{path}", new { id = "in_not-an-id" }, bob.Id)).Status
                .Should().Be(HttpStatusCode.NotFound, $"a malformed id cannot be {path}ed");
    }

    [Fact]
    public async Task Suspending_twice_and_accepting_nothing_are_no_ops_and_only_the_installer_changes_the_installation()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var hook = (await CreateAppAsync(ann.Id!, "bot", new { webhookUrl = ReceiverUrl })).GetProperty("app").GetProperty("webhookEndpointId").GetString()!;
        var installationId = await InstallAsync("bot", bob.Id!);
        await UntilSettledAsync(hook, 2);

        (await SendAsync("/spark/apps/installations/suspend", new { id = installationId }, ann.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "the App's owner does not manage bob's installation");

        var (suspended, view) = await SendAsync("/spark/apps/installations/suspend", new { id = installationId }, bob.Id);
        suspended.Should().Be(HttpStatusCode.OK);
        view.GetProperty("suspended").GetBoolean().Should().BeTrue();
        await UntilSettledAsync(hook, 3);
        var (again, againView) = await SendAsync("/spark/apps/installations/suspend", new { id = installationId }, bob.Id);
        again.Should().Be(HttpStatusCode.OK, "suspending a suspended installation is not an error");
        againView.GetProperty("suspended").GetBoolean().Should().BeTrue();
        var (accepted, acceptedView) = await SendAsync("/spark/apps/installations/accept", new { id = installationId }, bob.Id);
        accepted.Should().Be(HttpStatusCode.OK, "accepting with nothing pending is not an error");
        acceptedView.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal(["orders:read"]);

        (await DeliveriesAsync(hook)).Select(d => d.EventType).Should().BeEquivalentTo(["ping", "installation.created", "installation.suspend"]);
    }

    [Fact]
    public async Task Asking_for_less_narrows_every_installation_and_deleting_the_App_removes_them()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        await CreateAppAsync(ann.Id!, "bot", new { permissions = new[] { "orders:read", "invoices:read" } });
        var installationId = await InstallAsync("bot", bob.Id!);

        (await SendAsync("/spark/apps/apps/update", new { slug = "bot", permissions = new[] { "orders:read" } }, ann.Id)).Status
            .Should().Be(HttpStatusCode.OK);

        using (var session = Store.OpenAsyncSession())
        {
            var installation = (await session.LoadAsync<AppInstallation>(AppInstallation.IdPrefix + installationId))!;
            installation.Permissions.Should().Equal(["orders:read"], "what the App no longer asks for is withdrawn at once");
        }

        (await SendAsync("/spark/apps/apps/delete", new { slug = "bot" }, bob.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "an installer does not manage the App");
        (await SendAsync("/spark/apps/apps/delete", new { slug = "bot" }, ann.Id)).Status.Should().Be(HttpStatusCode.NoContent);

        using (var session = Store.OpenAsyncSession())
            (await session.Advanced.ExistsAsync(AppInstallation.IdPrefix + installationId)).Should().BeFalse("the installation goes with the App");
        var (_, bobs) = await GetAsync($"/spark/apps/installations?targetKind={WebhookOwner.UserKind}&targetKey={Uri.EscapeDataString(bob.Id!)}", bob.Id);
        bobs.GetArrayLength().Should().Be(0, "bob's account no longer lists it");
        (await GetAsync("/spark/apps/installations/new?slug=bot", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound, "the App is gone");
    }
}
