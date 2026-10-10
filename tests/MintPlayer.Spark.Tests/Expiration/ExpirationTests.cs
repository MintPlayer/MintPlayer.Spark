using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Expiration;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Expiration;

/// <summary>
/// Document expiration (<c>docs/seo_analyzer_PRD.md</c> X1, spike S6) through the real route table:
/// <c>@expires</c> is stamped on every store (pipeline and raw session), from the model's
/// <c>expiresAfter</c> or the entity's own <c>ExpiresAtUtc</c>; an expired document that RavenDB has not
/// swept yet is a missing row on every read path; the database is configured once, never more often than
/// the Community licence's 36 hours.
/// </summary>
/// <remarks>
/// RavenDB's sweep cannot be waited for in a test (36 hours). Each read test therefore also asserts the
/// expired document is still <b>stored</b>, so it is the filter — not a deletion — that hid it.
/// Fixture names start with <c>Ex</c>: <c>ActionsResolver</c> matches actions classes by simple name.
/// </remarks>
public class ExpirationTests : SparkTestDriver
{
    private static readonly Guid PageTypeId = Guid.Parse("0e5f0000-0000-4000-8000-000000000001");
    private static readonly Guid NoteTypeId = Guid.Parse("0e5f0000-0000-4000-8000-000000000002");
    private static readonly Guid FolderTypeId = Guid.Parse("0e5f0000-0000-4000-8000-000000000003");
    private static readonly Guid PagesQueryId = Guid.Parse("0e5f0000-0000-4000-8000-000000000011");
    private static readonly Guid PagesByFolderQueryId = Guid.Parse("0e5f0000-0000-4000-8000-000000000012");
    private static readonly TimeSpan PageLifetime = TimeSpan.FromDays(7);

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<Host> StartAsync(Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder>? spark = null)
    {
        var factory = new SparkEndpointFactory<ExContext>(
            Store,
            [PageModel(), NoteModel(), FolderModel()],
            configureServices: s => s.AddScoped<ExPageActions>(),
            configureSpark: spark,
            security: SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory.CreateClient(), cookie, xsrf);
    }

    // ---- stamping ------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_created_through_the_pipeline_expires_after_the_models_expiresAfter()
    {
        var host = await StartAsync();
        var before = DateTime.UtcNow;

        var (status, _) = await host.SendAsync("/spark/po/create", Wire.Typed(PageTypeId, new
        {
            persistentObject = new
            {
                name = "ExPage",
                objectTypeId = PageTypeId.ToString(),
                attributes = new[] { new { name = "Title", value = "created", isValueChanged = true } },
            },
        }));

        status.Should().Be(HttpStatusCode.Created);
        var (page, expires) = await LoadWithExpiresAsync<ExPage>(await IdOfPageAsync("created"));
        page.ExpiresAtUtc.HasValue.Should().BeTrue("the model declares expiresAfter 7 days");
        page.ExpiresAtUtc!.Value.Should().BeOnOrAfter(before + PageLifetime).And.BeOnOrBefore(DateTime.UtcNow + PageLifetime);
        Iso(expires).Should().Be(Iso(page.ExpiresAtUtc), "@expires mirrors the property");
    }

    [Fact]
    public async Task A_raw_session_store_outside_the_pipeline_is_stamped_too()
    {
        await StartAsync();
        var before = DateTime.UtcNow;

        var page = await SeedPageAsync("raw");

        var (stored, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        stored.ExpiresAtUtc!.Value.Should().BeOnOrAfter(before + PageLifetime);
        Iso(expires).Should().Be(Iso(stored.ExpiresAtUtc));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(14)]
    public async Task An_expiry_set_by_code_overrides_the_model_default(int days)
    {
        await StartAsync();
        var explicitExpiry = DateTime.UtcNow.AddDays(days);

        var page = await SeedPageAsync("explicit", explicitExpiry);

        var (stored, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        stored.ExpiresAtUtc!.Value.Should().BeCloseTo(explicitExpiry, TimeSpan.FromMilliseconds(1));
        Iso(expires).Should().Be(Iso(stored.ExpiresAtUtc));
    }

    [Fact]
    public async Task A_local_time_expiry_is_stored_as_UTC()
    {
        await StartAsync();
        var local = DateTime.Now.AddDays(2);

        var page = await SeedPageAsync("local", local);

        var (_, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        expires!.Value.Should().BeCloseTo(local.ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Clearing_the_expiry_of_a_stored_document_makes_it_permanent()
    {
        await StartAsync();
        var page = await SeedPageAsync("kept");
        (await LoadWithExpiresAsync<ExPage>(page.Id!)).Expires.HasValue.Should().BeTrue("stamped from expiresAfter first");

        using (var session = Store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<ExPage>(page.Id!);
            loaded.ExpiresAtUtc = null;
            await session.SaveChangesAsync();
        }

        var (stored, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        stored.ExpiresAtUtc.HasValue.Should().BeFalse("the model default applies to a new document only");
        expires.HasValue.Should().BeFalse("@expires goes with the property");
    }

    [Fact]
    public async Task An_edit_through_the_pipeline_keeps_the_expiry()
    {
        var host = await StartAsync();
        var page = await SeedPageAsync("before edit");
        var (original, _) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        original.ExpiresAtUtc.HasValue.Should().BeTrue("the raw store was stamped from expiresAfter");

        var (status, _) = await host.SendAsync("/spark/po/update", Wire.Typed(PageTypeId, new
        {
            persistentObject = new
            {
                id = page.Id,
                etag = await StoredEtag.OfAsync(Store, page.Id!),
                name = "ExPage",
                objectTypeId = PageTypeId.ToString(),
                attributes = new[] { new { name = "Title", value = "after edit", isValueChanged = true } },
            },
        }, page.Id));

        status.Should().Be(HttpStatusCode.OK);
        var (stored, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        stored.Title.Should().Be("after edit");
        Iso(stored.ExpiresAtUtc).Should().Be(Iso(original.ExpiresAtUtc), "an edit is not a new document");
        Iso(expires).Should().Be(Iso(original.ExpiresAtUtc));
    }

    // ---- D41: the client cannot set ExpiresAtUtc --------------------------------------------------

    [Theory]
    [InlineData("2099-01-01T00:00:00Z")]   // extend
    [InlineData(null)]                     // clear: keep the document forever
    public async Task A_client_edit_cannot_change_the_expiry(string? posted)
    {
        var host = await StartAsync();
        var page = await SeedPageAsync("edited by client");
        var (original, _) = await LoadWithExpiresAsync<ExPage>(page.Id!);

        var (status, _) = await host.SendAsync("/spark/po/update", Wire.Typed(PageTypeId, new
        {
            persistentObject = new
            {
                id = page.Id,
                etag = await StoredEtag.OfAsync(Store, page.Id!),
                name = "ExPage",
                objectTypeId = PageTypeId.ToString(),
                attributes = new object[]
                {
                    new { name = "Title", value = (object?)"changed", isValueChanged = true },
                    new { name = "ExpiresAtUtc", value = (object?)posted, isValueChanged = true },
                },
            },
        }, page.Id));

        status.Should().Be(HttpStatusCode.OK);
        var (stored, expires) = await LoadWithExpiresAsync<ExPage>(page.Id!);
        stored.Title.Should().Be("changed", "the rest of the edit is applied");
        Iso(stored.ExpiresAtUtc).Should().Be(Iso(original.ExpiresAtUtc), "ExpiresAtUtc is the framework's (D41)");
        Iso(expires).Should().Be(Iso(original.ExpiresAtUtc));
    }

    [Fact]
    public async Task A_client_create_cannot_choose_the_expiry()
    {
        var host = await StartAsync();
        var before = DateTime.UtcNow;

        var (status, _) = await host.SendAsync("/spark/po/create", Wire.Typed(PageTypeId, new
        {
            persistentObject = new
            {
                name = "ExPage",
                objectTypeId = PageTypeId.ToString(),
                attributes = new object[]
                {
                    new { name = "Title", value = (object?)"created by client", isValueChanged = true },
                    new { name = "ExpiresAtUtc", value = (object?)"2099-01-01T00:00:00Z", isValueChanged = true },
                },
            },
        }));

        status.Should().Be(HttpStatusCode.Created);
        var (page, expires) = await LoadWithExpiresAsync<ExPage>(await IdOfPageAsync("created by client"));
        page.ExpiresAtUtc!.Value.Should().BeOnOrAfter(before + PageLifetime).And.BeOnOrBefore(DateTime.UtcNow + PageLifetime, "the model's expiresAfter applies, not the posted value");
        Iso(expires).Should().Be(Iso(page.ExpiresAtUtc));
    }

    [Fact]
    public async Task Server_code_after_the_framework_still_sets_the_expiry()
    {
        var keep = ExKeepLonger.Until;
        var host = await StartAsync(spark => spark.AddInterceptor<ExKeepLonger>());
        var page = await SeedPageAsync("kept by code");

        var (status, _) = await host.SendAsync("/spark/po/update", Wire.Typed(PageTypeId, new
        {
            persistentObject = new
            {
                id = page.Id,
                etag = await StoredEtag.OfAsync(Store, page.Id!),
                name = "ExPage",
                objectTypeId = PageTypeId.ToString(),
                attributes = new[] { new { name = "Title", value = "code extends", isValueChanged = true } },
            },
        }, page.Id));

        status.Should().Be(HttpStatusCode.OK);
        (await LoadWithExpiresAsync<ExPage>(page.Id!)).Page.ExpiresAtUtc.Should().Be(keep);
    }

    /// <summary>Server code: an app interceptor registered after the framework's, so it may set the expiry.</summary>
    private sealed class ExKeepLonger : IBeforeSave
    {
        public static readonly DateTime Until = new(2098, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        public bool AppliesTo(Type entityType) => entityType == typeof(ExPage);

        public ValueTask OnBeforeSaveAsync(SaveContext context)
        {
            ((ExPage)context.Entity).ExpiresAtUtc = Until;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_type_without_expiresAfter_expires_only_when_code_says_so()
    {
        await StartAsync();
        var plain = new ExNote { Title = "no default" };
        var timed = new ExNote { Title = "timed", ExpiresAtUtc = DateTime.UtcNow.AddDays(2) };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(plain);
            await session.StoreAsync(timed);
            await session.SaveChangesAsync();
        }

        (await LoadWithExpiresAsync<ExNote>(plain.Id!)).Expires.HasValue.Should().BeFalse();
        Iso((await LoadWithExpiresAsync<ExNote>(timed.Id!)).Expires).Should().Be(Iso(timed.ExpiresAtUtc));
    }

    // ---- the read filter -------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_document_still_stored_is_a_404_on_load_and_missing_from_the_list()
    {
        var host = await StartAsync();
        var live = await SeedPageAsync("live");
        var expired = await SeedPageAsync("expired", DateTime.UtcNow.AddMinutes(-1));

        var (expiredStatus, _) = await host.SendAsync("/spark/po/load", Wire.Typed(PageTypeId, id: expired.Id));
        var (liveStatus, _) = await host.SendAsync("/spark/po/load", Wire.Typed(PageTypeId, id: live.Id));
        var ids = await host.QueryIdsAsync(Wire.Query(PagesQueryId));

        expiredStatus.Should().Be(HttpStatusCode.NotFound, "an expired row is a missing row (#453)");
        liveStatus.Should().Be(HttpStatusCode.OK);
        ids.Should().Equal(live.Id!);
        (await LoadWithExpiresAsync<ExPage>(expired.Id!)).Page.Should().NotBeNull("the document lingers until the sweep; the filter hid it");
    }

    [Fact]
    public async Task An_expired_document_is_missing_from_a_custom_sub_query_and_its_distinct_values()
    {
        var host = await StartAsync();
        var folder = new ExFolder { Name = "site" };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(folder);
            await session.SaveChangesAsync();
        }
        var live = await SeedPageAsync("live", folderId: folder.Id);
        await SeedPageAsync("expired", DateTime.UtcNow.AddMinutes(-1), folderId: folder.Id);

        var ids = await host.QueryIdsAsync(Wire.Query(PagesByFolderQueryId, new { parentId = folder.Id, parentType = "ExFolder" }));
        var (distinctStatus, distinct) = await host.SendAsync("/spark/queries/distinct-values",
            Wire.Query(PagesByFolderQueryId, new { column = "Title", parentId = folder.Id, parentType = "ExFolder" }));

        ids.Should().Equal(live.Id!);
        distinctStatus.Should().Be(HttpStatusCode.OK);
        distinct.GetRawText().Should().Contain("\"live\"").And.NotContain("\"expired\"");
    }

    [Fact]
    public async Task Documents_with_no_expiry_or_no_field_at_all_stay_visible()
    {
        var host = await StartAsync();
        var permanent = await SeedPageAsync("permanent");
        using (var session = Store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<ExPage>(permanent.Id!);
            loaded.ExpiresAtUtc = null;

            // Stored as a type without ExpiresAtUtc into the pages collection: the field is absent, the
            // shape a collection has when its type becomes IExpiring after documents exist.
            var legacy = new ExLegacyPage { Id = "ExPages/legacy", Title = "legacy" };
            await session.StoreAsync(legacy);
            session.Advanced.GetMetadataFor(legacy)[Constants.Documents.Metadata.Collection] = Store.Conventions.FindCollectionName(typeof(ExPage));
            session.Advanced.GetMetadataFor(legacy)[Constants.Documents.Metadata.RavenClrType] = typeof(ExPage).AssemblyQualifiedName;
            await session.SaveChangesAsync();
        }

        var ids = await host.QueryIdsAsync(Wire.Query(PagesQueryId));
        var (legacyLoad, _) = await host.SendAsync("/spark/po/load", Wire.Typed(PageTypeId, id: "ExPages/legacy"));

        ids.Should().BeEquivalentTo(["ExPages/legacy", permanent.Id!]); // !(ExpiresAtUtc <= now) matches null and absent alike
        legacyLoad.Should().Be(HttpStatusCode.OK);
    }

    // ---- the database --------------------------------------------------------------------------

    [Fact]
    public async Task Startup_enables_expiration_at_the_36_hour_floor()
    {
        await StartAsync();

        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        record.Expiration.Should().NotBeNull("an IExpiring model type requires expiration");
        record.Expiration!.Disabled.Should().BeFalse();
        record.Expiration.DeleteFrequencyInSec.Should().Be(36 * 60 * 60);
    }

    [Fact]
    public async Task A_delete_frequency_more_often_than_36_hours_refuses_startup_before_touching_the_database()
    {
        var act = () => StartAsync(spark => spark.AddExpiration(o => o.DeleteFrequency = TimeSpan.FromHours(1)));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("more often than every 36 hours");
        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));
        record.Expiration.Should().BeNull("nothing was sent");
    }

    [Fact]
    public async Task A_longer_frequency_from_configuration_is_used()
    {
        await StartAsync(spark => spark.AddExpiration(o => o.DeleteFrequency = TimeSpan.FromHours(48)));

        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        record.Expiration!.DeleteFrequencyInSec.Should().Be(48 * 60 * 60);
    }

    [Fact]
    public void ValidateDeleteFrequency_refuses_anything_below_36_hours_whatever_the_licence()
    {
        var below = () => SparkExpiration.ValidateDeleteFrequency(TimeSpan.FromHours(36) - TimeSpan.FromSeconds(1));
        below.Should().Throw<InvalidOperationException>();
        SparkExpiration.ValidateDeleteFrequency(TimeSpan.FromHours(36)).Should().Be(129_600);
    }

    [Fact]
    public async Task A_library_that_writes_expires_itself_gets_expiration_without_any_expiring_type()
    {
        await using var factory = new SparkEndpointFactory<ExPlainContext>(
            Store, [PlainModel()], configureSpark: spark => spark.Registry.RequireDocumentExpiration("a library"));

        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        record.Expiration!.DeleteFrequencyInSec.Should().Be(36 * 60 * 60);
    }

    [Fact]
    public async Task Nothing_that_needs_expiration_leaves_the_database_record_alone()
    {
        await using var factory = new SparkEndpointFactory<ExPlainContext>(Store, [PlainModel()]);

        var record = await Store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(Store.Database));

        record.Expiration.Should().BeNull();
    }

    // ---- startup checks ------------------------------------------------------------------------

    [Fact]
    public void ExpiresAfter_on_a_type_that_is_not_IExpiring_refuses_startup()
    {
        var model = PlainModel();
        model.PersistentObject.ExpiresAfter = TimeSpan.FromDays(1);

        var act = () => new SparkEndpointFactory<ExPlainContext>(Store, [model]);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("ExPlain declares \"expiresAfter\" but");
    }

    [Fact]
    public void An_explicitly_implemented_ExpiresAtUtc_refuses_startup()
    {
        var act = () => new SparkEndpointFactory<ExBrokenContext>(Store,
            [new EntityTypeFile { PersistentObject = new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "ExBroken", ClrType = typeof(ExBroken).FullName!, Attributes = [] } }]);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("ExBroken.ExpiresAtUtc is not a public property");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<ExPage> SeedPageAsync(string title, DateTime? expiresAtUtc = null, string? folderId = null)
    {
        var page = new ExPage { Title = title, ExpiresAtUtc = expiresAtUtc, FolderId = folderId };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(page);
        await session.SaveChangesAsync();
        return page;
    }

    private static string? Iso(DateTime? value) => value?.ToString("O");

    private async Task<string> IdOfPageAsync(string title)
    {
        using var session = Store.OpenAsyncSession();
        var page = await Raven.Client.Documents.LinqExtensions.SingleAsync(session.Query<ExPage>().Customize(c => c.WaitForNonStaleResults()).Where(p => p.Title == title));
        return page.Id!;
    }

    /// <summary>The stored entity and its <c>@expires</c> (null when absent).</summary>
    private async Task<(T Page, DateTime? Expires)> LoadWithExpiresAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        var entity = await session.LoadAsync<T>(id);
        if (entity is null)
            return (null!, null);
        var metadata = session.Advanced.GetMetadataFor(entity);
        DateTime? expires = metadata.TryGetValue(Constants.Documents.Metadata.Expires, out string? value)
            ? DateTime.Parse(value!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : null;
        return (entity, expires);
    }

    private static EntityTypeFile PageModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PageTypeId,
            Name = "ExPage",
            ClrType = typeof(ExPage).FullName!,
            Breadcrumb = "{Title}",
            ExpiresAfter = PageLifetime,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "FolderId", DataType = "Reference", ReferenceType = typeof(ExFolder).FullName },
                // Synchronised like any public property; D41 makes the pipeline ignore what a client posts for it.
                new() { Id = Guid.NewGuid(), Name = "ExpiresAtUtc", DataType = "datetime" },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = PagesQueryId, Name = "ExPages", Source = "Database.Pages", EntityType = "ExPage" },
            new SparkQuery { Id = PagesByFolderQueryId, Name = "ExPagesByFolder", Source = "Custom.PagesByFolder", EntityType = "ExPage" },
        ],
    };

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "ExNote",
            ClrType = typeof(ExNote).FullName!,
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" }],
        },
    };

    private static EntityTypeFile FolderModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = FolderTypeId,
            Name = "ExFolder",
            ClrType = typeof(ExFolder).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private static EntityTypeFile PlainModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = Guid.Parse("0e5f0000-0000-4000-8000-000000000004"),
            Name = "ExPlain",
            ClrType = typeof(ExPlain).FullName!,
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private sealed record Host(HttpClient Client, string Cookie, string Xsrf)
    {
        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Add("Cookie", Cookie);
            request.Headers.Add("X-XSRF-TOKEN", Xsrf);
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
        }

        public async Task<IReadOnlyList<string>> QueryIdsAsync(System.Text.Json.Nodes.JsonNode body)
        {
            var (status, result) = await SendAsync("/spark/queries/execute", body);
            status.Should().Be(HttpStatusCode.OK);
            return result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).OrderBy(i => i).ToList();
        }
    }
}

public class ExPage : IExpiring
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    [Reference(typeof(ExFolder))] public string? FolderId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>A page as stored before the type was expiring: no <c>ExpiresAtUtc</c> at all.</summary>
public class ExLegacyPage
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class ExNote : IExpiring
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
}

public class ExFolder
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class ExPlain
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class ExBroken : IExpiring
{
    public string? Id { get; set; }
    DateTime? IExpiring.ExpiresAtUtc { get; set; }
}

public class ExContext : SparkContext
{
    public IRavenQueryable<ExPage> Pages => Session.Query<ExPage>();
    public IRavenQueryable<ExNote> Notes => Session.Query<ExNote>();
    public IRavenQueryable<ExFolder> Folders => Session.Query<ExFolder>();
}

public class ExPlainContext : SparkContext
{
    public IRavenQueryable<ExPlain> Plains => Session.Query<ExPlain>();
}

public class ExBrokenContext : SparkContext
{
    public IRavenQueryable<ExBroken> Broken => Session.Query<ExBroken>();
}

/// <summary>The sub-query on a folder's page: its pages, from a raw session query — the filter still applies.</summary>
public class ExPageActions(IEntityMapper mapper, IAsyncDocumentSession session) : DefaultPersistentObjectActions<ExPage>(mapper)
{
    public IRavenQueryable<ExPage> PagesByFolder(MintPlayer.Spark.Queries.CustomQueryArgs args)
    {
        ArgumentNullException.ThrowIfNull(args.Parent);
        return session.Query<ExPage>().Where(p => p.FolderId == args.Parent!.Id);
    }
}
