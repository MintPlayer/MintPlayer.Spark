using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// Contributions M5b, PRD Q7: the generated contributions query (the History link's target) leaves out
/// every hidden contribution — withdrawn by its author, hidden by a revert, hidden by a moderator — by
/// default, and lists them only for <c>ViewDeleted</c> holders asking with <c>deleted=include|only</c>.
/// Without <c>ViewDeleted</c> the flag is ignored silently (SoftDelete's row policy), so the answer is
/// the default list. Fixtures are the generated <c>CoSong.Lyrics</c> types of <see cref="ContributionsRuntimeTests"/>.
/// </summary>
public class ContributionsHiddenVisibilityTests : SparkTestDriver
{
    private static readonly Guid SongTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000001");
    private static readonly Guid LyricsTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000002");
    private static readonly Guid ContributionTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000003");
    private static readonly Guid CurrentTypeId = Guid.Parse("c0d20000-0000-4000-8000-c0d200000004");
    private const string SongId = "CoSongs/1";
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";
    private const string Carol = "users/carol";
    private const string Dave = "users/dave";
    private const string Moderator = "users/moderator";

    private static readonly string QueryAlias = SparkQueryAliases.Derive(CoSongLyricsContributionMetadata.QueryName);

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

    private sealed record Host(SparkClient Client, CoIdentity Identity);

    private async Task<Host> StartAsync(SparkTestSecurity? security = null)
    {
        await SeedAsync(session => session.StoreAsync(new CoSong { Title = "Gangnam Style" }, SongId));

        var identity = new CoIdentity { Id = Alice };
        var factory = new SparkEndpointFactory(
            Store,
            [SongModel(), LyricsModel(), ContributionModel(), CurrentModel()],
            configureServices: s =>
            {
                s.AddSingleton(identity);
                s.AddSingleton(new CoAuditLog());
                s.AddScoped<ISparkCurrentUser, CoCurrentUser>();
                s.AddScoped<ISatelliteAuditSink, CoAuditSink>();
                s.AddScoped<ISparkUserNameResolver, CoNames>();
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
        return new Host(client, identity);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static async Task<PO> LoadSongAsync(Host host)
        => await host.Client.GetPersistentObjectAsync(SongTypeId, SongId) ?? throw new InvalidOperationException("song not visible");

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

    private static async Task SaveRowsAsync(Host host, Func<List<PO>, List<PO>> edit)
    {
        var po = await LoadSongAsync(host);
        var lyrics = (PersistentObjectAttributeAsDetail)po["Lyrics"];
        lyrics.Objects = edit([.. lyrics.Objects ?? []]);
        lyrics.IsValueChanged = true;
        await host.Client.UpdatePersistentObjectAsync(po);
    }

    private static Task AddAsync(Host host, string language, string script, string text)
        => SaveRowsAsync(host, rows => [.. rows, NewRow(language, script, text)]);

    private static Task EditTextAsync(Host host, string key, string text) => SaveRowsAsync(host, rows =>
    {
        var row = rows.Single(r => r.Id == key);
        row["Text"].Value = text;
        row["Text"].IsValueChanged = true;
        return rows;
    });

    private static Task RemoveAsync(Host host, string key) => SaveRowsAsync(host, rows => [.. rows.Where(r => r.Id != key)]);

    private static string ContributionId(string language, string script, string user) => CoSongLyricsContribution.GetId(SongId, language, script, user);

    private static async Task<HttpStatusCode> RevertAsync(Host host, string contributionId)
    {
        var response = await host.Client.SendAsync(HttpMethod.Post, "/spark/po/revert-contribution",
            JsonContent.Create(Wire.Typed(ContributionTypeId, id: contributionId)), requiresAntiforgery: true);
        return response.StatusCode;
    }

    /// <summary>
    /// Four contributions to <c>en/Latn</c> and one to <c>ko/Kore</c>, three of the first hidden in the
    /// three ways a contribution gets hidden:
    /// <list type="bullet">
    ///   <item>Alice's: a moderator's <c>Delete</c> (hidden);</item>
    ///   <item>Carol's: newer than Bob's when the moderator reverts to Bob's (reverted);</item>
    ///   <item>Dave's: Dave removes his row (withdrawn), after which Bob's is current again;</item>
    /// </list>
    /// visible: Bob's <c>en/Latn</c> and Bob's <c>ko/Kore</c>.
    /// </summary>
    private static async Task HideInEveryWayAsync(Host host)
    {
        host.Identity.Id = Alice;
        await AddAsync(host, "en", "Latn", "alice's text");
        host.Identity.Id = Bob;
        await EditTextAsync(host, "en/Latn", "bob's text");
        await AddAsync(host, "ko", "Kore", "bob's korean");
        host.Identity.Id = Carol;
        await EditTextAsync(host, "en/Latn", "carol's text");

        host.Identity.Id = Moderator;
        (await RevertAsync(host, ContributionId("en", "Latn", Bob))).Should().Be(HttpStatusCode.OK);
        await host.Client.DeleteAsLoadedAsync(ContributionTypeId, ContributionId("en", "Latn", Alice));

        host.Identity.Id = Dave;
        await EditTextAsync(host, "en/Latn", "dave's text");
        await RemoveAsync(host, "en/Latn");
    }

    private static async Task<List<string>> ListAsync(Host host, SparkDeletedFilter? deleted = null)
        => [.. (await host.Client.ExecuteQueryAsync(QueryAlias, parentId: SongId, parentType: "CoSong", deleted: deleted))
            .Items.Select(i => i.Id!).Order(StringComparer.Ordinal)];

    // ---- tests ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Hidden_contributions_are_left_out_of_the_contributions_query_by_default()
    {
        var host = await StartAsync();
        await HideInEveryWayAsync(host);

        var visible = Sorted(ContributionId("en", "Latn", Bob), ContributionId("ko", "Kore", Bob));

        host.Identity.Id = Moderator;
        (await ListAsync(host)).Should().Equal(visible, "withdrawn, reverted and moderator-hidden versions are all left out");
    }

    private static string[] Sorted(params string[] ids) => [.. ids.Order(StringComparer.Ordinal)];

    [Fact]
    public async Task ViewDeleted_holders_see_the_hidden_contributions_with_deleted_include_or_only()
    {
        var host = await StartAsync();
        await HideInEveryWayAsync(host);
        var hidden = Sorted(ContributionId("en", "Latn", Alice), ContributionId("en", "Latn", Carol), ContributionId("en", "Latn", Dave));
        var all = Sorted([.. hidden, ContributionId("en", "Latn", Bob), ContributionId("ko", "Kore", Bob)]);

        host.Identity.Id = Moderator;
        (await ListAsync(host, SparkDeletedFilter.Include)).Should().Equal(all, "include lists hidden and visible versions");
        (await ListAsync(host, SparkDeletedFilter.Only)).Should().Equal(hidden, "only lists the hidden ones");
    }

    [Fact]
    public async Task Without_ViewDeleted_the_deleted_flag_is_ignored_and_hidden_contributions_stay_out()
    {
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/CoSongLyricsContribution"));
        await HideInEveryWayAsync(host);
        var visible = Sorted(ContributionId("en", "Latn", Bob), ContributionId("ko", "Kore", Bob));

        host.Identity.Id = Bob;
        (await ListAsync(host, SparkDeletedFilter.Include)).Should().Equal(visible, "the flag is ignored without ViewDeleted");
        (await ListAsync(host, SparkDeletedFilter.Only)).Should().Equal(visible, "an ignored flag is the default list, not an empty one");
    }

    // ---- models (as in ContributionsSurfaceTests) -------------------------------------------------------

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
