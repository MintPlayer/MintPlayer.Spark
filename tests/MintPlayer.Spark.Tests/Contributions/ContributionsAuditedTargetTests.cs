using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Contributions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// Contributions M6 — the shape the QnA demo needs (<c>Question.Translations</c>): an <see cref="IAuditable"/>
/// target under History and SoftDelete, whose own attributes only its owner writes
/// (<c>GetProtectedAttributesAsync("Edit")</c>), while anyone holding <c>Edit</c> on the type may add their
/// version of a row.
/// </summary>
/// <remarks>
/// Found by wiring the demo: History stamped <c>ModifiedBy</c>/<c>ModifiedAt</c> on every edit, so a
/// contribution-only save rewrote the target (a new etag and revision, the translator named as its
/// modifier) — against R3. Fixture names start with <c>Ca</c> (the actions resolver matches by simple
/// name across the assembly).
/// </remarks>
public class ContributionsAuditedTargetTests : SparkTestDriver
{
    private static readonly Guid PostTypeId = Guid.Parse("c0d60000-0000-4000-8000-c0d600000001");
    private static readonly Guid NoteTypeId = Guid.Parse("c0d60000-0000-4000-8000-c0d600000002");
    private const string PostId = "CaPosts/1";
    private const string Owner = "users/owner";
    private const string Translator = "users/translator";
    private static readonly DateTimeOffset Stamped = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    private async Task<Host> StartAsync()
    {
        await SeedAsync(session => session.StoreAsync(new CaPost
        {
            Title = "original",
            OwnerId = Owner,
            CreatedBy = Owner,
            CreatedAt = Stamped,
            ModifiedBy = Owner,
            ModifiedAt = Stamped,
        }, PostId));

        var identity = new CoIdentity { Id = Translator };
        var factory = new SparkEndpointFactory(
            Store,
            [PostModel(), NoteModel()],
            configureServices: s =>
            {
                s.AddSingleton(identity);
                s.AddScoped<ISparkCurrentUser, CoCurrentUser>();
                s.AddScoped<CaPostActions>();
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                spark.AddHistory();
                spark.AddContributions(typeof(CaPost).Assembly);
            });
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);
        return new Host(client, identity);
    }

    private static async Task SaveAsync(Host host, Action<PO> edit)
    {
        var po = await host.Client.GetPersistentObjectAsync(PostTypeId, PostId) ?? throw new InvalidOperationException("post not visible");
        edit(po);
        await host.Client.UpdatePersistentObjectAsync(po);
    }

    private static void AddNote(PO po, string language, string text)
    {
        var notes = (PersistentObjectAttributeAsDetail)po["Notes"];
        notes.Objects = [.. notes.Objects ?? [], new PO
        {
            Name = "CaNote",
            ObjectTypeId = NoteTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Language", DataType = "string", Value = language, IsValueChanged = true },
                new PersistentObjectAttribute { Name = "Text", DataType = "string", Value = text, IsValueChanged = true },
            ],
        }];
        notes.IsValueChanged = true;
    }

    private async Task<(CaPost Post, string? ChangeVector)> ReadPostAsync()
    {
        using var session = Store.OpenAsyncSession();
        var post = await session.LoadAsync<CaPost>(PostId);
        return (post, session.Advanced.GetChangeVectorFor(post));
    }

    [Fact]
    public async Task A_contribution_alone_leaves_an_audited_target_untouched()
    {
        var host = await StartAsync();
        var (_, before) = await ReadPostAsync();

        await SaveAsync(host, po => AddNote(po, "nl", "vertaling"));

        var (post, after) = await ReadPostAsync();
        after.Should().Be(before, "only the contribution and the current document were written (R3)");
        post.ModifiedBy.Should().Be(Owner, "a translator is not the post's modifier");
        post.ModifiedAt.Should().Be(Stamped);

        using var session = Store.OpenAsyncSession();
        var contribution = await session.LoadAsync<CaPostNotesContribution>(CaPostNotesContribution.GetId(PostId, "nl", Translator));
        contribution!.Text.Should().Be("vertaling");
        (await session.LoadAsync<CaPostNotesCurrent>(CaPostNotesCurrent.GetId(PostId, "nl")))!.ContributorId.Should().Be(Translator);
    }

    [Fact]
    public async Task A_non_owner_writes_their_row_while_the_owner_only_title_is_dropped()
    {
        var host = await StartAsync();
        var (_, before) = await ReadPostAsync();

        await SaveAsync(host, po =>
        {
            po["Title"].Value = "hijacked";
            po["Title"].IsValueChanged = true;
            AddNote(po, "fr", "traduction");
        });

        var (post, after) = await ReadPostAsync();
        post.Title.Should().Be("original", "the per-row hook protects the title from a non-owner");
        after.Should().Be(before);
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<CaPostNotesCurrent>(CaPostNotesCurrent.GetId(PostId, "fr")))!.Text.Should().Be("traduction");
    }

    [Fact]
    public async Task The_owners_real_edit_is_still_stamped()
    {
        var host = await StartAsync();
        host.Identity.Id = Owner;

        await SaveAsync(host, po =>
        {
            po["Title"].Value = "edited";
            po["Title"].IsValueChanged = true;
        });

        var (post, _) = await ReadPostAsync();
        post.Title.Should().Be("edited");
        post.ModifiedBy.Should().Be(Owner);
        post.ModifiedAt.Should().NotBe(Stamped, "an edit that changes the document is stamped as before");
        post.CreatedAt.Should().Be(Stamped);
    }

    // ---- models ---------------------------------------------------------------------------------------

    private static EntityTypeFile PostModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PostTypeId,
            Name = "CaPost",
            ClrType = typeof(CaPost).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Notes", DataType = "AsDetail", AsDetailType = typeof(CaNote).FullName, IsArray = true },
            ],
        },
    };

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "CaNote",
            ClrType = typeof(CaNote).FullName!,
            Breadcrumb = "{Language}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Language", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Text", DataType = "string" },
            ],
        },
    };
}

// ---- fixtures (generated: CaPostNotesContribution, CaPostNotesCurrent, CaPostNotesContributionMetadata, CaNote.Key) ----

public class CaPost : IAuditable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OwnerId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }

    [Contribution]
    [Newtonsoft.Json.JsonIgnore]
    public List<CaNote> Notes { get; set; } = [];
}

public partial class CaNote
{
    [ContributionSlot] public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>QnA's rule in miniature: everyone may edit (to contribute), only the owner writes the title.</summary>
public class CaPostActions(IEntityMapper mapper, CoIdentity identity) : DefaultPersistentObjectActions<CaPost>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: the per-row hook is the rule under test.";

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, CaPost entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(action == "Edit" && entity.OwnerId != identity.Id ? ["Title"] : null);
}
