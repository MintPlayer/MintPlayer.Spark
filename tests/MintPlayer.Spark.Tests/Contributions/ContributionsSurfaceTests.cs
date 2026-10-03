using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// Contributions M5b (server half): removing a whole version (Delete on the current type), the
/// <c>RevertContribution</c> endpoint, contributor names in the rows, the generated contributions query
/// (the History link's target), the Q3 save notices, and the moderator audit through
/// <see cref="ISatelliteAuditSink"/>. Fixtures are the generated <c>CoSong.Lyrics</c> types of
/// <see cref="ContributionsRuntimeTests"/>.
/// </summary>
public class ContributionsSurfaceTests : SparkTestDriver
{
    private static readonly Guid SongTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000001");
    private static readonly Guid LyricsTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000002");
    private static readonly Guid ContributionTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000003");
    private static readonly Guid CurrentTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000004");
    private const string SongId = "CoSongs/1";
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";
    private const string Carol = "users/carol";
    private const string Moderator = "users/moderator";

    private readonly List<IAsyncDisposable> factories = [];
    private readonly List<IDisposable> clients = [];

    public override async Task DisposeAsync()
    {
        foreach (var client in clients)
            client.Dispose();
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private sealed record Host(SparkEndpointFactory Factory, SparkClient Client, CoIdentity Identity, CoAuditLog Audit);

    private async Task<Host> StartAsync(bool names = true, SparkTestSecurity? security = null, Action<IServiceCollection>? services = null)
    {
        await SeedAsync(session => session.StoreAsync(new CoSong { Title = "Gangnam Style" }, SongId));

        var identity = new CoIdentity { Id = Alice };
        var audit = new CoAuditLog();
        var factory = new SparkEndpointFactory(
            Store,
            [SongModel(), LyricsModel(), ContributionModel(), CurrentModel()],
            configureServices: s =>
            {
                s.AddSingleton(identity);
                s.AddSingleton(audit);
                s.AddScoped<ISparkCurrentUser, CoCurrentUser>();
                s.AddScoped<ISatelliteAuditSink, CoAuditSink>();
                if (names)
                    s.AddScoped<ISparkUserNameResolver, CoNames>();
                services?.Invoke(s);
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                spark.AddContributions(typeof(CoSong).Assembly);
            },
            security: security);
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);
        return new Host(factory, client, identity, audit);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static async Task<PO> LoadSongAsync(Host host)
        => await host.Client.GetPersistentObjectAsync(SongTypeId, SongId) ?? throw new InvalidOperationException("song not visible");

    private static PersistentObjectAttributeAsDetail Lyrics(PO po) => (PersistentObjectAttributeAsDetail)po["Lyrics"];

    private static PO NewRow(string language, string script, string text) => new()
    {
        Name = "CoLyrics",
        ObjectTypeId = LyricsTypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Language", DataType = "string", Value = language, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Script", DataType = "string", Value = script, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Text", DataType = "string", Value = text, IsValueChanged = true },
        ],
    };

    /// <summary>Saves the song's rows as edited, as the current identity; returns the notices of the response.</summary>
    private static async Task<List<SparkNotifyOperation>> SaveRowsAsync(Host host, Func<List<PO>, List<PO>> edit)
    {
        var po = await LoadSongAsync(host);
        var lyrics = Lyrics(po);
        lyrics.Objects = edit([.. lyrics.Objects ?? []]);
        lyrics.IsValueChanged = true;
        var notices = new List<SparkNotifyOperation>();
        await host.Client.UpdatePersistentObjectAsync(po, onOperation: op =>
        {
            if (op is SparkNotifyOperation notify)
                notices.Add(notify);
        });
        return notices;
    }

    private static Task<List<SparkNotifyOperation>> AddAsync(Host host, string language, string script, string text)
        => SaveRowsAsync(host, rows => [.. rows, NewRow(language, script, text)]);

    private static Task<List<SparkNotifyOperation>> EditTextAsync(Host host, string key, string text) => SaveRowsAsync(host, rows =>
    {
        var row = rows.Single(r => r.Id == key);
        row["Text"].Value = text;
        row["Text"].IsValueChanged = true;
        return rows;
    });

    private static Task<List<SparkNotifyOperation>> RemoveAsync(Host host, string key) => SaveRowsAsync(host, rows => [.. rows.Where(r => r.Id != key)]);

    private async Task<T?> ReadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private static string ContributionId(string language, string script, string user) => CoSongLyricsContribution.GetId(SongId, language, script, user);
    private static string CurrentId(string language, string script) => CoSongLyricsCurrent.GetId(SongId, language, script);

    private static async Task<(HttpStatusCode Status, string Body)> RevertAsync(Host host, string contributionId)
    {
        var response = await host.Client.SendAsync(HttpMethod.Post, "/spark/po/revert-contribution",
            JsonContent.Create(Wire.Typed(ContributionTypeId, id: contributionId)), requiresAntiforgery: true);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Alice, then Bob, then Carol write <c>en/Latn</c>: Carol's is current, three in the history.</summary>
    private static async Task ThreeVersionsAsync(Host host)
    {
        host.Identity.Id = Alice;
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await EditTextAsync(host, "en/Latn", "bob's text");
        host.Identity.Id = Carol;
        await EditTextAsync(host, "en/Latn", "carol's text");
    }

    // ---- Q6: attribution data -----------------------------------------------------------------------

    [Fact]
    public async Task Rows_carry_the_contributor_name_resolved_at_read_time_the_date_and_the_count()
    {
        var host = await StartAsync();
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await EditTextAsync(host, "en/Latn", "bob's text");
        await AddAsync(host, "ko", "Kore", "bob's korean");

        var rows = Lyrics(await LoadSongAsync(host)).Objects!;

        var en = rows.Single(r => r.Id == "en/Latn");
        en["ContributorName"].Value?.ToString().Should().Be("Bob");
        en["UpdatedAt"].Value.Should().NotBeNull();
        Convert.ToInt32(en["ContributionCount"].Value?.ToString()).Should().Be(2);
        rows.Single(r => r.Id == "ko/Kore")["ContributorName"].Value?.ToString().Should().Be("Bob");

        var current = await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
        JsonSerializer.Serialize(current).Should().NotContain("Bob", "names are resolved at read time, never stored");
    }

    [Fact]
    public async Task Without_a_name_resolver_the_contributor_name_stays_empty()
    {
        var host = await StartAsync(names: false);
        await AddAsync(host, "en", "Latn", "alice's text");

        var row = Lyrics(await LoadSongAsync(host)).Objects!.Single();

        row["ContributorName"].Value.Should().BeNull("there is no core fallback that could invent a name");
        row["UpdatedAt"].Value.Should().NotBeNull();
    }

    // ---- Q3: notices on the save response -----------------------------------------------------------

    [Fact]
    public async Task Withdrawing_my_version_says_whose_version_is_shown_now_or_that_none_is_left()
    {
        var host = await StartAsync();
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        (await EditTextAsync(host, "en/Latn", "bob's text")).Should().BeEmpty("an edit withdraws nothing");

        var notices = await RemoveAsync(host, "en/Latn");

        notices.Should().ContainSingle();
        notices[0].Message.Should().Be("Your version of en/Latn was withdrawn; Alice's version is shown now.");
        notices[0].Kind.Should().Be(NotificationKind.Info);

        host.Identity.Id = Alice;
        var last = await RemoveAsync(host, "en/Latn");
        last.Should().ContainSingle().Which.Message.Should().Be("Your version of en/Latn was withdrawn; no version of it is left.");
    }

    [Fact]
    public async Task Removing_or_moving_someone_elses_version_says_it_stays()
    {
        var host = await StartAsync();
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;

        var removed = await RemoveAsync(host, "en/Latn");
        removed.Should().ContainSingle().Which.Kind.Should().Be(NotificationKind.Warning);
        removed[0].Message.Should().Be("en/Latn shows Alice's version, which only its author or a moderator can remove: it stays.");

        // Editing the row's slot is withdraw-old plus add-new: Alice's en/Latn stays and reappears.
        var moved = await SaveRowsAsync(host, rows =>
        {
            var row = rows.Single(r => r.Id == "en/Latn");
            row["Language"].Value = "fr";
            row["Language"].IsValueChanged = true;
            return rows;
        });
        moved.Should().ContainSingle().Which.Message.Should().StartWith("en/Latn shows Alice's version");
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.ContributorId.Should().Be(Alice);
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("fr", "Latn")))!.ContributorId.Should().Be(Bob);
    }

    [Fact]
    public async Task Without_a_name_the_notice_is_generic()
    {
        var host = await StartAsync(names: false);
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await EditTextAsync(host, "en/Latn", "bob's text");

        var notices = await RemoveAsync(host, "en/Latn");

        notices.Should().ContainSingle().Which.Message.Should().Be("Your version of en/Latn was withdrawn; another contributor's version is shown now.");
    }

    /// <summary>
    /// The browser asks for Dutch (Accept-Language) while the user picked English in the app, which
    /// ng-spark keeps to itself: the notice must carry every language so the client can show English.
    /// </summary>
    [Fact]
    public async Task A_notice_carries_every_language_so_the_app_shows_the_one_its_user_picked()
    {
        var host = await StartAsync();
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await EditTextAsync(host, "en/Latn", "bob's text");
        host.Client.AcceptLanguage = "nl";

        var notices = await RemoveAsync(host, "en/Latn");

        var notice = notices.Should().ContainSingle().Which;
        notice.Raw.TryGetProperty("translatedMessage", out var translations).Should().BeTrue("the client resolves the notice in the app-chosen language");
        translations.GetProperty("en").GetString().Should().Be("Your version of en/Latn was withdrawn; Alice's version is shown now.");
        translations.GetProperty("nl").GetString().Should().Be("Uw versie van en/Latn is ingetrokken; nu wordt de versie van Alice getoond.");
        translations.GetProperty("fr").GetString().Should().Be("Votre version de en/Latn a été retirée ; la version de Alice est maintenant affichée.");
    }

    // ---- Q3/Q4: remove a whole version --------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_current_document_hides_every_version_of_the_slot_audited_and_restorable()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);
        host.Identity.Id = Bob;
        await AddAsync(host, "ko", "Kore", "untouched");

        host.Identity.Id = Moderator;
        await host.Client.DeleteAsLoadedAsync(CurrentTypeId, CurrentId("en", "Latn"));

        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"))).Should().BeNull("the version is removed");
        foreach (var user in new[] { Alice, Bob, Carol })
        {
            var hidden = await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", user));
            hidden!.IsDeleted.Should().BeTrue();
            hidden.DeleteReason.Should().Be("version-removed");
            hidden.DeletedBy.Should().Be(Moderator);
            hidden.ContributorId.Should().Be(user, "nothing is re-attributed");
        }
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("ko", "Kore"))).Should().NotBeNull("another slot is untouched");
        Lyrics(await LoadSongAsync(host)).Objects!.Select(r => r.Id).Should().Equal("ko/Kore");

        var entry = host.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("RemoveContributionVersion");
        entry.DocumentId.Should().Be(CurrentId("en", "Latn"));
        entry.TargetId.Should().Be(SongId);
        entry.AffectedDocumentIds.Should().Equal(ContributionId("en", "Latn", Alice), ContributionId("en", "Latn", Bob), ContributionId("en", "Latn", Carol));

        // Restoring one brings the slot back with that version.
        var restored = await host.Client.SendAsync(HttpMethod.Post, "/spark/po/restore",
            JsonContent.Create(Wire.Typed(ContributionTypeId, id: ContributionId("en", "Latn", Bob))), requiresAntiforgery: true);
        restored.StatusCode.Should().Be(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.ContributorId.Should().Be(Bob);
    }

    [Fact]
    public async Task Removing_a_version_needs_Delete_on_the_current_type()
    {
        var host = await StartAsync(security: SparkTestSecurity.Permissive.Denying("Delete/CoSongLyricsCurrent"));
        await AddAsync(host, "en", "Latn", "alice's text");

        host.Identity.Id = Moderator;
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => host.Client.DeleteAsLoadedAsync(CurrentTypeId, CurrentId("en", "Latn")));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound, "a refusal is indistinguishable from a missing row");
        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Alice)))!.IsDeleted.Should().BeFalse();
        host.Audit.Entries.Should().BeEmpty();
    }

    // ---- Q4: RevertContribution ---------------------------------------------------------------------

    [Fact]
    public async Task Reverting_hides_every_newer_version_atomically_and_never_reattributes()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);
        var alicesBefore = await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Alice));

        host.Identity.Id = Moderator;
        var (status, body) = await RevertAsync(host, ContributionId("en", "Latn", Alice));

        status.Should().Be(HttpStatusCode.OK, body);
        var current = await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
        current!.ContributorId.Should().Be(Alice);
        current.Text.Should().Be("alice's text");
        current.ContributionCount.Should().Be(1);
        current.UpdatedAt.Should().Be(alicesBefore!.UpdatedAt, "the reverted-to version keeps its own date");

        var alices = await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Alice));
        alices!.Text.Should().Be("alice's text");
        alices.ContributorId.Should().Be(Alice);
        alices.UpdatedAt.Should().Be(alicesBefore.UpdatedAt, "the text is never rewritten");
        foreach (var user in new[] { Bob, Carol })
        {
            var hidden = await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", user));
            hidden!.IsDeleted.Should().BeTrue();
            hidden.DeleteReason.Should().Be("reverted");
            hidden.ContributorId.Should().Be(user);
        }

        var entry = host.Audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be(ContributionRights.RevertContribution);
        entry.DocumentId.Should().Be(ContributionId("en", "Latn", Alice));
        entry.AffectedDocumentIds.Should().Equal(ContributionId("en", "Latn", Bob), ContributionId("en", "Latn", Carol));
        entry.Reason.Should().Be("reverted");

        host.Identity.Id = Alice;
        Lyrics(await LoadSongAsync(host)).Objects!.Single()["Text"].Value?.ToString().Should().Be("alice's text");
    }

    [Fact]
    public async Task Reverting_to_a_middle_version_keeps_the_older_ones()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);

        host.Identity.Id = Moderator;
        (await RevertAsync(host, ContributionId("en", "Latn", Bob))).Status.Should().Be(HttpStatusCode.OK);

        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Alice)))!.IsDeleted.Should().BeFalse("it is older");
        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Carol)))!.IsDeleted.Should().BeTrue();
        var current = await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
        current!.ContributorId.Should().Be(Bob);
        current.ContributionCount.Should().Be(2);
    }

    [Fact]
    public async Task Reverting_needs_the_RevertContribution_right()
    {
        var host = await StartAsync(security: SparkTestSecurity.Permissive.Denying("RevertContribution/CoSongLyricsContribution"));
        await ThreeVersionsAsync(host);

        host.Identity.Id = Moderator;
        var (status, _) = await RevertAsync(host, ContributionId("en", "Latn", Alice));

        status.Should().Be(HttpStatusCode.NotFound, "Spark's standard refusal: 404, indistinguishable from a missing row");
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.ContributorId.Should().Be(Carol);
        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Carol)))!.IsDeleted.Should().BeFalse();
        host.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reverting_to_a_hidden_version_is_refused_until_it_is_restored()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);
        host.Identity.Id = Moderator;
        await host.Client.DeleteAsLoadedAsync(ContributionTypeId, ContributionId("en", "Latn", Alice));

        var (status, body) = await RevertAsync(host, ContributionId("en", "Latn", Alice));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("Restore it");
        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Carol)))!.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_hide_reverts_nothing()
    {
        var host = await StartAsync(services: s => s.AddScoped<ISatelliteWriteGuard>(sp => new CoGuardFor(ContributionId("en", "Latn", Carol), sp.GetRequiredService<CoIdentity>())));
        await ThreeVersionsAsync(host); // the guard binds only the moderator, on Carol's document

        host.Identity.Id = Moderator;
        var (status, body) = await RevertAsync(host, ContributionId("en", "Latn", Alice));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        (await ReadAsync<CoSongLyricsContribution>(ContributionId("en", "Latn", Bob)))!.IsDeleted.Should().BeFalse("one transaction: Bob's hide rolled back with Carol's refusal");
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.ContributorId.Should().Be(Carol);
        host.Audit.Entries.Should().BeEmpty();
    }

    // ---- Q7: the generated contributions query (the History link) -----------------------------------

    [Fact]
    public async Task The_history_link_lists_the_slots_contributions_newest_first_with_names()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);
        host.Identity.Id = Bob;
        await AddAsync(host, "ko", "Kore", "bob's korean");

        // The link contract: query alias + parentId/parentType + one column filter per slot.
        var result = await host.Client.ExecuteQueryAsync(
            SparkQueryAliases.Derive(CoSongLyricsContributionMetadata.QueryName),
            parentId: SongId, parentType: "CoSong",
            columns:
            [
                new QueryColumnFilter { Name = "Language", Includes = ["en"] },
                new QueryColumnFilter { Name = "Script", Includes = ["Latn"] },
            ]);

        result.Items.Select(i => i.Id).Should().Equal(
            ContributionId("en", "Latn", Carol), ContributionId("en", "Latn", Bob), ContributionId("en", "Latn", Alice));
        result.Items.Select(i => i.Values.Single(v => v.Key == "ContributorName").Value?.ToString())
            .Should().Equal("Carol", "Bob", "Alice");

        var all = await host.Client.ExecuteQueryAsync(SparkQueryAliases.Derive(CoSongLyricsContributionMetadata.QueryName), parentId: SongId, parentType: "CoSong");
        all.Items.Should().HaveCount(4, "without the slot filter: every slot of the song");

        var orphan = await host.Client.ExecuteQueryAsync(SparkQueryAliases.Derive(CoSongLyricsContributionMetadata.QueryName));
        orphan.Items.Should().BeEmpty("the history of a target is only reachable through the target");
    }

    [Fact]
    public void The_attribution_renderer_options_carry_the_history_link_contract()
    {
        var options = ContributionDescriptor.AttributionRendererOptions(CoSongLyricsContributionMetadata.Instance);

        options["contributionsQuery"].Should().Be("cosonglyricscontributions");
        options["targetType"].Should().Be("CoSong");
        options["property"].Should().Be("Lyrics");
        ((string[])options["slots"]).Should().Equal("Language", "Script");
        ((string[])options["attribution"]).Should().Equal("ContributorName", "UpdatedAt", "ContributionCount");
    }

    // ---- M5b client contract: own-row marker, revert right, revert notice ---------------------------

    [Fact]
    public async Task Each_row_says_whether_the_caller_wrote_the_version_it_shows_never_who()
    {
        var host = await StartAsync();
        host.Identity.Id = Alice;
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await AddAsync(host, "ko", "Kore", "bob's korean");

        static bool? Own(PO row) => row.Metadata is { } metadata && metadata.TryGetValue("contribution", out var facts)
            ? ((JsonElement)facts!).GetProperty("own").GetBoolean()
            : null;

        host.Identity.Id = Alice;
        var rows = Lyrics(await LoadSongAsync(host)).Objects!;
        Own(rows.Single(r => r.Id == "en/Latn")).Should().BeTrue();
        Own(rows.Single(r => r.Id == "ko/Kore")).Should().BeFalse();

        host.Identity.Id = Carol;
        Lyrics(await LoadSongAsync(host)).Objects!.Select(Own).Where(own => own != false).Should().BeEmpty();

        var raw = await (await host.Client.SendAsync(HttpMethod.Post, "/spark/po/load",
            JsonContent.Create(new { objectTypeId = SongTypeId.ToString(), id = SongId }))).Content.ReadAsStringAsync();
        raw.Should().Contain("\"own\"");
        raw.Should().NotContain(Alice).And.NotContain(Bob, "the marker is a boolean; the raw contributor ids never reach the target's page");
    }

    [Fact]
    public async Task Permissions_report_RevertContribution()
    {
        var granted = await StartAsync();
        (await granted.Client.GetPermissionsAsync(ContributionTypeId.ToString()))!.CanRevertContribution.Should().BeTrue();

        var denied = await StartAsync(security: SparkTestSecurity.Permissive.Denying("RevertContribution/CoSongLyricsContribution"));
        (await denied.Client.GetPermissionsAsync(ContributionTypeId.ToString()))!.CanRevertContribution.Should().BeFalse();
    }

    [Fact]
    public async Task A_revert_answers_with_a_notice_of_what_it_hid()
    {
        var host = await StartAsync();
        await ThreeVersionsAsync(host);

        host.Identity.Id = Moderator;
        var (status, body) = await RevertAsync(host, ContributionId("en", "Latn", Alice));

        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"notify\"").And.Contain("2 newer version(s) were hidden");
        body.Should().Contain("\"translatedMessage\"").And.Contain("2 nieuwere versie(s) werden verborgen", "every language travels; the client picks");
    }

    // ---- models ---------------------------------------------------------------------------------------

    private static EntityTypeFile SongModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SongTypeId,
            Name = "CoSong",
            ClrType = typeof(CoSong).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Lyrics", DataType = "AsDetail", AsDetailType = typeof(CoLyrics).FullName, IsArray = true },
            ],
        },
    };

    private static EntityTypeFile LyricsModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LyricsTypeId,
            Name = "CoLyrics",
            ClrType = typeof(CoLyrics).FullName!,
            Breadcrumb = "{Language}/{Script}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Language", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Script", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Text", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ContributorName", DataType = "string", IsReadOnly = true, Renderer = ContributionDescriptor.AttributionRenderingHint },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "UpdatedAt", DataType = "datetime", IsReadOnly = true, Renderer = ContributionDescriptor.AttributionRenderingHint },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ContributionCount", DataType = "number", IsReadOnly = true, Renderer = ContributionDescriptor.AttributionRenderingHint },
            ],
        },
    };

    private static EntityTypeFile ContributionModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ContributionTypeId,
            Name = "CoSongLyricsContribution",
            ClrType = typeof(CoSongLyricsContribution).FullName!,
            Breadcrumb = "{Language}/{Script}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Language", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Script", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Text", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ContributorName", DataType = "string", IsReadOnly = true },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "UpdatedAt", DataType = "datetime" },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = Guid.Parse("c0d20000-0000-4000-8000-c0d200000010"),
                Name = CoSongLyricsContributionMetadata.QueryName,
                EntityType = "CoSongLyricsContribution",
                Source = "Custom." + ContributionDescriptor.ContributionsQueryMethod,
                SortColumns = [new SortColumn { Property = "UpdatedAt", Direction = "desc" }],
            },
        ],
    };

    private static EntityTypeFile CurrentModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CurrentTypeId,
            Name = "CoSongLyricsCurrent",
            ClrType = typeof(CoSongLyricsCurrent).FullName!,
            Breadcrumb = "{Language}/{Script}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Language", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Script", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Text", DataType = "string" },
            ],
        },
    };
}

/// <summary>The app's name lookup (one batched call), as History's resolver would be registered.</summary>
public sealed class CoNames : ISparkUserNameResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(userIds
            .Where(id => id.StartsWith("users/", StringComparison.Ordinal) && id != "users/moderator")
            .ToDictionary(id => id, id => char.ToUpperInvariant(id[6]) + id[7..]));
}

public sealed class CoAuditLog
{
    public List<SatelliteAuditEntry> Entries { get; } = [];
}

public sealed class CoAuditSink(CoAuditLog log) : ISatelliteAuditSink
{
    public ValueTask RecordAsync(SatelliteAuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (log.Entries)
            log.Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Refuses the moderator's writes to one document (a "locked" contribution).</summary>
public sealed class CoGuardFor(string lockedId, CoIdentity identity) : ISatelliteWriteGuard
{
    public ValueTask EnsureMayWriteAsync(SatelliteWriteContext context)
        => identity.Id == "users/moderator" && string.Equals(context.DocumentId, lockedId, StringComparison.OrdinalIgnoreCase)
            ? throw new SparkValidationException($"{context.DocumentId} is locked.")
            : ValueTask.CompletedTask;
}
