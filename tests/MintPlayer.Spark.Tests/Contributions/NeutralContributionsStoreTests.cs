using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// #536: Contributions on the database <c>SPARK_TEST_DATABASE</c> picks. A save writes the caller's contribution and the
/// current document through the neutral session; hydration reads the current documents by prefix; the rebuild and the
/// startup shape check repair the current documents from the contributions. The fixtures are
/// <see cref="ContributionsRuntimeTests"/>'.
/// </summary>
public class NeutralContributionsStoreTests : SparkTestDriver
{
    private const string SongId = "cosongs/1";
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";

    private SparkEndpointFactory? factory;
    private SparkClient? client;
    private readonly CoIdentity identity = new() { Id = Alice };

    public override async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<SparkClient> StartAsync()
    {
        factory = new SparkEndpointFactory(
            Database,
            [ContributionsRuntimeTests.SongModel(), ContributionsRuntimeTests.LyricsModel(), ContributionsRuntimeTests.ContributionModel()],
            configureServices: s =>
            {
                s.AddSingleton(identity);
                s.AddScoped<ISparkCurrentPrincipal, CoCurrentUser>();
            },
            configureSpark: spark => spark.AddContributions(typeof(CoSong).Assembly));
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new CoSong { Title = "Gangnam Style" }, SongId);
            await session.SaveChangesAsync();
        }
        client = new SparkClient(factory.CreateClient(), ownsClient: true);
        return client;
    }

    private ISparkStore Store => factory!.GetService<ISparkStore>();

    private async Task<T?> ReadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenSession();
        return await session.LoadAsync<T>(id);
    }

    private static string CurrentId(string language, string script) => CoSongLyricsCurrent.GetId(SongId, language, script);

    private static async Task SaveRowsAsync(SparkClient client, Func<List<PO>, List<PO>> edit)
    {
        var po = (await client.GetPersistentObjectAsync(ContributionsRuntimeTests.SongTypeId, SongId))!;
        var lyrics = (PersistentObjectAttributeAsDetail)po["Lyrics"];
        lyrics.Objects = edit([.. lyrics.Objects ?? []]);
        lyrics.IsValueChanged = true;
        await client.UpdatePersistentObjectAsync(po);
    }

    private static PO Row(string language, string script, string text) => new()
    {
        Name = "CoLyrics",
        ObjectTypeId = Guid.Parse("c0d10000-0000-4000-8000-c0d100000002"),
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Language", DataType = "string", Value = language, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Script", DataType = "string", Value = script, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Text", DataType = "string", Value = text, IsValueChanged = true },
        ],
    };

    private async Task AliceThenBobAsync(SparkClient client)
    {
        await SaveRowsAsync(client, rows => [.. rows, Row("en", "Latn", "alice's text")]);
        identity.Id = Bob;
        await SaveRowsAsync(client, rows =>
        {
            var row = rows.Single(r => r.Id == "en/Latn");
            row["Text"].Value = "bob's text";
            row["Text"].IsValueChanged = true;
            return rows;
        });
    }

    [Fact]
    public void The_generated_id_prefixes_are_the_collections_the_store_names()
    {
        CoSongLyricsContribution.CollectionPrefix.Should().Be(SparkCollectionNames.Of(typeof(CoSongLyricsContribution)).ToLowerInvariant() + "/");
        CoSongLyricsCurrent.CollectionPrefix.Should().Be(SparkCollectionNames.Of(typeof(CoSongLyricsCurrent)).ToLowerInvariant() + "/");
    }

    [Fact]
    public async Task The_latest_contribution_is_current_and_the_song_shows_it()
    {
        var client = await StartAsync();

        await AliceThenBobAsync(client);

        var current = await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
        current!.Text.Should().Be("bob's text");
        current.ContributorId.Should().Be(Bob);
        current.ContributionCount.Should().Be(2);
        (await ReadAsync<CoSongLyricsContribution>(CoSongLyricsContribution.GetId(SongId, "en", "Latn", Alice)))!.Text.Should().Be("alice's text");
        var po = (await client.GetPersistentObjectAsync(ContributionsRuntimeTests.SongTypeId, SongId))!;
        ((PersistentObjectAttributeAsDetail)po["Lyrics"]).Objects!.Single()["Text"].Value?.ToString().Should().Be("bob's text");
    }

    [Fact]
    public async Task The_rebuild_and_the_shape_check_repair_the_current_documents()
    {
        var client = await StartAsync();
        await AliceThenBobAsync(client);
        using (var session = Store.OpenSession())
        {
            var current = await session.LoadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
            current!.Text = "corrupted";
            await session.StoreAsync(new CoSongLyricsCurrent { TargetId = SongId, Language = "fr", Script = "Latn", Text = "orphan" }, CurrentId("fr", "Latn"));
            await session.SaveChangesAsync();
        }

        using (var scope = factory!.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IContributions>().RebuildCurrentAsync(SongId);

        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.Text.Should().Be("bob's text");
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("fr", "Latn"))).Should().BeNull("no contribution backs it");

        // A stale marker: the shape check finds the target by streaming the collections and rebuilds it.
        using (var session = Store.OpenSession())
        {
            var current = await session.LoadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn"));
            current!.Text = "stale";
            await session.StoreAsync(new ContributionShapeMarker { Shape = "0000000000000000" }, ContributionShapeMarker.IdFor(CoSongLyricsContributionMetadata.Instance).Value);
            await session.SaveChangesAsync();
        }
        var check = factory.GetService<IEnumerable<IHostedService>>().OfType<ContributionShapeCheck>().Single();
        (await check.RunAsync(CancellationToken.None)).Should().Be(1);
        (await ReadAsync<CoSongLyricsCurrent>(CurrentId("en", "Latn")))!.Text.Should().Be("bob's text");
    }
}
