using MintPlayer.Spark.Tests._Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// "Vm" fixtures: ActionsResolver matches actions classes by simple name across the whole assembly.
public class VmItem
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
}

public class VmSong
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public List<VmLyric> Lyrics { get; set; } = [];
}

public class VmLyric
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string Language { get; set; } = "";
}

public class VmItemActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VmItem>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public class VmSongActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VmSong>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public class VmLyricActions(IEntityMapper mapper) : DefaultPersistentObjectActions<VmLyric>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public sealed class VmPingAction : ICustomAction
{
    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class VmContext : SparkContext
{
    public IRavenQueryable<VmItem> Items => Session.Query<VmItem>();
    public IRavenQueryable<VmSong> Songs => Session.Query<VmSong>();
}

/// <summary>
/// W6 (contributions M2c-2b): the verb matrix of attribute-level rights over HTTP — Query, Read, Edit
/// and New × type-level and attribute-level × allow, deny and important — the lyrics-contributor
/// scenario, and Delete and custom actions staying type-level. (The attribute segment on Delete or a
/// custom action is refused by the validator; <see cref="AttributeRightsTests"/> pins that.)
/// </summary>
/// <remarks>
/// A rule is written <c>+Verb/Type[/Attr]</c> (allow), <c>-…</c> (deny), with a leading <c>!</c> for
/// important. Each case observes the four verbs on <c>VmItem.Code</c>: the query column, the GET
/// attribute, whether an update writes it, whether a create writes it.
/// </remarks>
public class AttributeVerbMatrixTests : SparkTestDriver
{
    private static readonly Guid ItemTypeId = Guid.Parse("a77c0000-0000-4000-8000-00000000c001");
    private static readonly Guid SongTypeId = Guid.Parse("a77c0000-0000-4000-8000-00000000c002");
    private static readonly Guid LyricTypeId = Guid.Parse("a77c0000-0000-4000-8000-00000000c003");
    private static readonly Guid ItemsQueryId = Guid.Parse("a77c0000-0000-4000-8000-00000000c004");
    private const string Ping = "VmPing";

    static AttributeVerbMatrixTests()
        => SparkValueObjects.Register(typeof(VmLyric), "Id", row => ((VmLyric)row).Id);

    private SparkEndpointFactory<VmContext>? factory;
    private SparkClient? client;

    public override async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<SparkClient> StartAsync(params string[] rules)
    {
        factory = new SparkEndpointFactory<VmContext>(
            Store,
            [ItemModel(), SongModel(), LyricModel()],
            configureServices: services =>
            {
                services.AddScoped<VmPingAction>();
                services.AddScoped<VmItemActions>();
                services.AddScoped<VmSongActions>();
                services.AddScoped<VmLyricActions>();
                services.AddSingleton(TestActions.LoaderWithCustom(Ping));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(Ping, sp.GetRequiredService<VmPingAction>()));
            },
            security: SparkTestSecurity.FromJson(Security(rules)));
        client = new SparkClient(factory.CreateClient(), ownsClient: true);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new VmItem { Name = "a", Code = "k0" }, "vmitems/1");
            await session.StoreAsync(new VmSong
            {
                Title = "t0",
                Artist = "ar0",
                Lyrics = [new VmLyric { Id = "y1", Text = "old", Language = "en" }],
            }, "vmsongs/1");
        });
        return client;
    }

    public static TheoryData<string, string[], bool, bool, bool, bool> Matrix => new()
    {
        { "type grant only: the attribute inherits it", ["+QueryReadEditNew/VmItem"], true, true, true, true },
        { "QueryRead denied on the attribute: write-only", ["+QueryReadEditNew/VmItem", "-QueryRead/VmItem/Code"], false, false, true, true },
        { "Query denied on the attribute", ["+QueryReadEditNew/VmItem", "-Query/VmItem/Code"], false, true, true, true },
        { "Read denied on the attribute", ["+QueryReadEditNew/VmItem", "-Read/VmItem/Code"], true, false, true, true },
        { "Edit denied on the attribute", ["+QueryReadEditNew/VmItem", "-Edit/VmItem/Code"], true, true, false, true },
        { "New denied on the attribute", ["+QueryReadEditNew/VmItem", "-New/VmItem/Code"], true, true, true, false },
        { "an attribute grant without the type grant unlocks nothing", ["+QueryRead/VmItem", "+New/VmItem", "+Edit/VmItem/Code"], true, true, false, true },
        { "deny beats allow", ["+QueryReadEditNew/VmItem", "-Edit/VmItem/Code", "+Edit/VmItem/Code"], true, true, false, true },
        { "important allow beats deny", ["+QueryReadEditNew/VmItem", "-Edit/VmItem/Code", "!+Edit/VmItem/Code"], true, true, true, true },
        { "important deny beats important allow", ["+QueryReadEditNew/VmItem", "!-Edit/VmItem/Code", "!+Edit/VmItem/Code"], true, true, false, true },
        { "an important type deny is not undone by an attribute grant", ["+QueryReadEditNew/VmItem", "!-Edit/VmItem", "+Edit/VmItem/Code"], true, true, false, true },
        { "an important attribute allow cannot unlock a denied type", ["+QueryReadEditNew/VmItem", "-Edit/VmItem", "!+Edit/VmItem/Code"], true, true, false, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_effective_attribute_right_decides_each_verb(string what, string[] rules, bool query, bool read, bool edit, bool create)
    {
        var spark = await StartAsync(rules);

        var columns = (await spark.ExecuteQueryAsync(ItemsQueryId)).Columns.Select(c => c.Name);
        columns.Contains("Code").Should().Be(query, $"Query ({what})");

        var loaded = await spark.GetPersistentObjectAsync(ItemTypeId, "vmitems/1");
        loaded!.Attributes.Any(a => a.Name == "Code").Should().Be(read, $"Read ({what})");

        Set(loaded, "Code", "edited");
        var updated = await SucceedsAsync(() => spark.UpdatePersistentObjectAsync(loaded));
        (updated && (await LoadAsync<VmItem>("vmitems/1")).Code == "edited").Should().Be(edit, $"Edit ({what})");

        string? createdId = null;
        var created = await SucceedsAsync(async () =>
        {
            var scaffold = await spark.NewPersistentObjectAsync(ItemTypeId);
            Set(scaffold, "Name", "fresh");
            Set(scaffold, "Code", "created");
            createdId = (await spark.CreatePersistentObjectAsync(scaffold)).Id;
        });
        (created && (await LoadAsync<VmItem>(createdId!)).Code == "created").Should().Be(create, $"New ({what})");
    }

    // ---- the contributor (PRD §5 Q13) --------------------------------------------------------------

    /// <summary>
    /// <c>Edit/VmSong</c> plus denies on its other attributes, and <c>Edit/VmLyric/Text</c> on the row
    /// type: a lyrics row's Text is all that changes. Without the row type's own <c>Edit</c> right the
    /// attribute grant unlocks nothing and the row stays as stored.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_contributor_changes_a_lyrics_rows_Text_and_nothing_else(bool rowTypeGranted)
    {
        string[] rules =
        [
            "+QueryRead/VmSong", "+Edit/VmSong", "-Edit/VmSong/Title", "-Edit/VmSong/Artist",
            "+Edit/VmLyric/Text", "-Edit/VmLyric/Language",
            .. rowTypeGranted ? new[] { "+Edit/VmLyric" } : [],
        ];
        var spark = await StartAsync(rules);

        var song = await spark.GetPersistentObjectAsync(SongTypeId, "vmsongs/1");
        Set(song!, "Title", "hijacked");
        Set(song!, "Artist", "hijacked");
        var lyrics = (PersistentObjectAttributeAsDetail)song!["Lyrics"];
        var row = lyrics.Objects!.Single();
        Set(row, "Text", "new");
        Set(row, "Language", "nl");
        lyrics.IsValueChanged = true;

        await spark.UpdatePersistentObjectAsync(song);

        var stored = await LoadAsync<VmSong>("vmsongs/1");
        stored.Title.Should().Be("t0");
        stored.Artist.Should().Be("ar0");
        stored.Lyrics.Single().Language.Should().Be("en");
        stored.Lyrics.Single().Text.Should().Be(rowTypeGranted ? "new" : "old");
    }

    // ---- Delete and custom actions stay type-level --------------------------------------------------

    [Fact]
    public async Task Attribute_denials_on_every_attribute_do_not_restrict_Delete_or_a_custom_action()
    {
        var spark = await StartAsync(
            "+QueryReadEditNewDelete/VmItem", $"+{Ping}/VmItem",
            "-Edit/VmItem/Name", "-Edit/VmItem/Code", "-QueryRead/VmItem/Code");

        var action = await spark.ExecuteActionAsync(ItemTypeId, Ping);
        await spark.DeletePersistentObjectAsync(ItemTypeId, "vmitems/1");

        action.StatusCode.Should().Be(200);
        (await LoadOrNullAsync<VmItem>("vmitems/1")).Should().BeNull();
    }

    [Fact]
    public async Task Attribute_grants_never_enable_Delete_or_a_custom_action()
    {
        var spark = await StartAsync("+QueryReadEditNew/VmItem", "+Edit/VmItem/Name", "+Edit/VmItem/Code");

        var action = await ActionRunsAsync(spark);
        var deleted = await SucceedsAsync(() => spark.DeletePersistentObjectAsync(ItemTypeId, "vmitems/1"));

        action.Should().BeFalse();
        deleted.Should().BeFalse();
        (await LoadOrNullAsync<VmItem>("vmitems/1")).Should().NotBeNull();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static async Task<bool> ActionRunsAsync(SparkClient spark)
    {
        try
        {
            return (await spark.ExecuteActionAsync(ItemTypeId, Ping)).StatusCode == 200;
        }
        catch (SparkClientException)
        {
            return false;
        }
    }

    private static async Task<bool> SucceedsAsync(Func<Task> act)
    {
        try
        {
            await act();
            return true;
        }
        catch (SparkClientException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<T> LoadAsync<T>(string id) where T : class
        => (await LoadOrNullAsync<T>(id))!;

    private async Task<T?> LoadOrNullAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private static void Set(Po po, string name, object? value)
    {
        var attribute = po.Attributes.FirstOrDefault(a => a.Name == name);
        if (attribute is null)
        {
            attribute = new PersistentObjectAttribute { Name = name, DataType = "string" };
            po.AddAttribute(attribute);
        }
        attribute.Value = value;
        attribute.IsValueChanged = true;
        attribute.IsReadOnly = false;
    }

    /// <summary>The rules as a security.json granting to both well-known groups.</summary>
    private static string Security(string[] rules)
    {
        var rights = new List<Right>();
        foreach (var rule in rules)
        {
            var important = rule.StartsWith('!');
            var body = important ? rule[1..] : rule;
            var denied = body[0] == '-';
            var resource = body[1..];
            foreach (var group in new[] { SparkTestSecurity.AnonymousGroupId, SparkTestSecurity.AuthenticatedGroupId })
            {
                rights.Add(new Right
                {
                    Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{rule}|{group}"))),
                    Resource = resource,
                    GroupId = group,
                    IsDenied = denied,
                    IsImportant = important,
                });
            }
        }

        return JsonSerializer.Serialize(new SecurityConfiguration
        {
            WellKnown = new Dictionary<string, string>
            {
                [SparkWellKnownGroups.Anonymous] = SparkTestSecurity.AnonymousGroupId.ToString(),
                [SparkWellKnownGroups.Authenticated] = SparkTestSecurity.AuthenticatedGroupId.ToString(),
            },
            Groups = new Dictionary<string, string>
            {
                [SparkTestSecurity.AnonymousGroupId.ToString()] = "Anonymous visitors",
                [SparkTestSecurity.AuthenticatedGroupId.ToString()] = "Signed-in users",
            },
            Rights = rights,
        });
    }

    private static EntityTypeFile ItemModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ItemTypeId,
            Name = "VmItem",
            ClrType = typeof(VmItem).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Code", DataType = "string", Order = 2 },
            ],
        },
        Queries = [new SparkQuery { Id = ItemsQueryId, Name = "VmItems", Source = "Database.Items", EntityType = "VmItem" }],
    };

    private static EntityTypeFile SongModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SongTypeId,
            Name = "VmSong",
            ClrType = typeof(VmSong).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Artist", DataType = "string", Order = 2 },
                new() { Id = Guid.NewGuid(), Name = "Lyrics", DataType = "AsDetail", AsDetailType = typeof(VmLyric).FullName, IsArray = true, Order = 3 },
            ],
        },
    };

    private static EntityTypeFile LyricModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LyricTypeId,
            Name = "VmLyric",
            ClrType = typeof(VmLyric).FullName!,
            Breadcrumb = "{Text}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Language", DataType = "string", Order = 2 },
            ],
        },
    };

    private sealed class StubActionResolver(string name, ICustomAction action) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string requested)
            => string.Equals(requested, name, StringComparison.OrdinalIgnoreCase) ? action : null;

        public IReadOnlyList<string> GetRegisteredActionNames() => [name];
    }
}
