using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Sharing;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Sharing;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Organizations;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Sharing;

/// <summary>
/// X6 (<c>docs/seo_analyzer_PRD.md</c>, D31): a capability-token read of one persistent object. Organization Owners
/// and Admins make and revoke a link; anyone holding it reads exactly the declared attributes and the declared child
/// query of that object, anonymously, until it is revoked. Every refusal is one 404.
/// </summary>
public class ShareLinkTests : OrganizationsTestHost
{
    private static readonly Guid NoteTypeId = Guid.Parse("05ae0000-0000-4000-8000-000000000001");
    private static readonly Guid CommentTypeId = Guid.Parse("05ae0000-0000-4000-8000-000000000002");
    private static readonly Guid CommentsQueryId = Guid.Parse("05ae0000-0000-4000-8000-000000000011");
    private static readonly Guid AllCommentsQueryId = Guid.Parse("05ae0000-0000-4000-8000-000000000012");

    private bool sharingOn = true;

    public ShareLinkTests()
    {
        ExtraModels.Add(NoteModel());
        ExtraModels.Add(CommentModel());
        ExtraRights.Add(new Right { Key = "sh-notes", Resource = "QueryReadEditNewDelete/ShNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraRights.Add(new Right { Key = "sh-comments", Resource = "QueryRead/ShComment", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraRights.Add(new Right { Key = "sh-share", Resource = "ShareLink/ShNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraRights.Add(new Right { Key = "sh-revoke", Resource = "RevokeShareLink/ShNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraServices = services =>
        {
            services.AddScoped<ShNoteActions>();
            services.AddScoped<ShCommentActions>();
            services.AddSingleton<ISparkShareGate>(new Gate(() => sharingOn));
        };
        ExtraSpark = spark => spark.AddShareLinks(o =>
        {
            o.RequestsPerMinute = 1000;
            o.PublicBaseUrl = PublicBaseUrl;
            o.Share<ShNote>(
                attributes: ["Title"],
                queries: new Dictionary<string, string[]> { ["ShNoteComments"] = ["Text"], ["AllShComments"] = ["Text"] });
        });
    }

    [Fact]
    public async Task An_Admin_shares_and_anyone_with_the_link_reads_exactly_the_declared_attributes_and_children()
    {
        var (ann, _, _, note, other) = await SeedAsync();

        var link = await ShareAsync(note.Id!, ann.Id!);
        var shared = await LoadSharedAsync(link);

        shared.Status.Should().Be(HttpStatusCode.OK);
        shared.Body.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Should().Equal("Title");
        shared.Body.GetProperty("attributes")[0].GetProperty("value").GetString().Should().Be("Launch plan");
        var comments = shared.Body.GetProperty("queries").GetProperty("ShNoteComments");
        comments.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).OrderBy(i => i).Should().Equal("ShComments/1", "ShComments/2");
        comments.GetProperty("items")[0].GetProperty("values").EnumerateArray().Select(v => v.GetProperty("key").GetString()).Should().Equal("Text");
        comments.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Equal("Text");
        shared.Body.GetProperty("queries").TryGetProperty("AllShComments", out _).Should().BeFalse("a query the type does not declare as its sub-query is never run, even when listed");
        foreach (var leak in new[] { "Secret", "budget", "Author", other.Id!, "ShareTokenHash", "elsewhere" })
            shared.Raw.Should().NotContain(leak);

        var page = await SendAsync("/spark/share/query", new { link.Type, link.Id, link.Token, query = "ShNoteComments", skip = 1, take = 1 }, null);
        page.Status.Should().Be(HttpStatusCode.OK);
        page.Body.GetProperty("items").GetArrayLength().Should().Be(1);
        page.Body.GetProperty("totalItems").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// The page heads the object and each child table with the model's labels (M6 leftover, fixed in M7): the type's
    /// label and each declared child query's, translated like every other label, never the raw query name.
    /// </summary>
    [Fact]
    public async Task The_share_carries_the_type_label_and_a_label_per_child_query()
    {
        var (ann, _, _, note, _) = await SeedAsync();

        var shared = await LoadSharedAsync(await ShareAsync(note.Id!, ann.Id!));

        shared.Status.Should().Be(HttpStatusCode.OK);
        // No translations here, so the labels are the model loader's humanized names: what a page shows for them too.
        Texts(shared.Body.GetProperty("label")).Should().Contain("Sh Note");
        var labels = shared.Body.GetProperty("queryLabels");
        Texts(labels.GetProperty("ShNoteComments")).Should().Contain("Sh Note Comments");
        labels.TryGetProperty("AllShComments", out _).Should().BeFalse("only the queries the link shows are named");

        static List<string> Texts(JsonElement translated) => translated.ValueKind == JsonValueKind.Object
            ? translated.EnumerateObject().Select(p => p.Value.GetString() ?? "").ToList()
            : [];
    }

    [Fact]
    public async Task A_Member_may_not_share_and_a_stranger_cannot_even_see_the_action_target()
    {
        var (_, max, out_, note, _) = await SeedAsync();

        (await ExecuteAsync("ShareLink", note.Id!, max.Id!)).Status.Should().Be(HttpStatusCode.NotFound, "D31: Owners and Admins only; the row gate answers like a missing row");
        (await ExecuteAsync("ShareLink", note.Id!, out_.Id!)).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync<ShNote>(note.Id!))!.ShareTokenHash.Should().BeNull();
    }

    [Fact]
    public async Task Every_refusal_is_the_same_404()
    {
        var (ann, _, _, note, other) = await SeedAsync();
        var link = await ShareAsync(note.Id!, ann.Id!);

        var refusals = new[]
        {
            await LoadSharedAsync(link with { Token = link.Token[..^2] + "AA" }),
            await LoadSharedAsync(link with { Id = other.Id! }),
            await LoadSharedAsync(link with { Type = "OgNote" }),
            await LoadSharedAsync(link with { Type = "Nope" }),
            await LoadSharedAsync(link with { Token = "" }),
        };
        refusals.Select(r => r.Status).Distinct().Should().Equal(HttpStatusCode.NotFound);
        refusals.Select(r => r.Raw).Distinct().Count().Should().Be(1);
        (await SendAsync("/spark/share/query", new { link.Type, link.Id, link.Token, query = "AllShComments" }, null)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revoking_or_replacing_a_link_ends_it_at_once()
    {
        var (ann, _, _, note, _) = await SeedAsync();
        var first = await ShareAsync(note.Id!, ann.Id!);
        var second = await ShareAsync(note.Id!, ann.Id!);

        (await LoadSharedAsync(first)).Status.Should().Be(HttpStatusCode.NotFound, "a new link replaces the old one");
        (await LoadSharedAsync(second)).Status.Should().Be(HttpStatusCode.OK);

        (await ExecuteAsync("RevokeShareLink", note.Id!, ann.Id!)).Status.Should().Be(HttpStatusCode.OK);
        (await LoadSharedAsync(second)).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync<ShNote>(note.Id!))!.ShareTokenHash.Should().BeNull();
    }

    [Fact]
    public async Task Switching_sharing_off_refuses_new_links_and_hides_existing_ones()
    {
        var (ann, _, _, note, _) = await SeedAsync();
        var link = await ShareAsync(note.Id!, ann.Id!);

        sharingOn = false;
        (await LoadSharedAsync(link)).Status.Should().Be(HttpStatusCode.NotFound);
        var refused = await ExecuteAsync("ShareLink", note.Id!, ann.Id!);
        refused.Raw.Should().Contain("Sharing is switched off");
        refused.Raw.Should().NotContain("/shared/");

        sharingOn = true;
        (await LoadSharedAsync(link)).Status.Should().Be(HttpStatusCode.OK, "switching back on restores the same link");
    }

    [Fact]
    public async Task An_expired_object_is_gone_from_its_link_too()
    {
        var (ann, _, _, note, _) = await SeedAsync();
        var link = await ShareAsync(note.Id!, ann.Id!);
        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<ShNote>(note.Id);
            stored.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await session.SaveChangesAsync();
        }

        (await LoadSharedAsync(link)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_client_can_neither_set_nor_clear_the_token_hash_through_an_edit()
    {
        var (ann, _, _, note, _) = await SeedAsync();
        var link = await ShareAsync(note.Id!, ann.Id!);
        var hash = (await LoadAsync<ShNote>(note.Id!))!.ShareTokenHash;

        var (status, updateBody) = await SendAsync("/spark/po/update", Wire.Typed(NoteTypeId, new
        {
            persistentObject = new
            {
                id = note.Id,
                etag = await StoredEtag.OfAsync(Store, note.Id!),
                objectTypeId = NoteTypeId,
                name = "ShNote",
                attributes = new object[]
                {
                    new { name = "Title", value = "Renamed", dataType = "string", isValueChanged = true },
                    new { name = "OrganizationId", value = note.OrganizationId, dataType = "string" },
                    new { name = "ShareTokenHash", value = "attacker-chosen", dataType = "string", isValueChanged = true },
                },
            },
        }, id: note.Id), ann.Id);

        status.Should().Be(HttpStatusCode.OK, updateBody.ValueKind == JsonValueKind.Undefined ? "" : updateBody.GetRawText());
        var stored = await LoadAsync<ShNote>(note.Id!);
        stored!.Title.Should().Be("Renamed");
        stored.ShareTokenHash.Should().Be(hash);
        (await LoadSharedAsync(link)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Share_reads_are_rate_limited_per_address()
    {
        ExtraSpark += spark => spark.AddShareLinks(o => o.RequestsPerMinute = 3);
        var (ann, _, _, note, _) = await SeedAsync();
        var link = await ShareAsync(note.Id!, ann.Id!);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await LoadSharedAsync(link)).Status);

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    internal sealed record Link(string Type, string Id, string Token);

    private async Task<(SparkUser Ann, SparkUser Max, SparkUser Out, ShNote Note, ShNote Other)> SeedAsync()
    {
        var ann = await SeedUserAsync("ann");
        var max = await SeedUserAsync("max");
        var out_ = await SeedUserAsync("out");
        var acme = await SeedOrganizationAsync("Acme", ann, (max, OrganizationRole.Member));
        var globex = await SeedOrganizationAsync("Globex", out_);
        using var session = Store.OpenAsyncSession();
        var note = new ShNote { Id = "ShNotes/1", Title = "Launch plan", Secret = "budget 1M", OrganizationId = acme.Id };
        var other = new ShNote { Id = "ShNotes/2", Title = "Other", Secret = "x", OrganizationId = globex.Id };
        await session.StoreAsync(note);
        await session.StoreAsync(other);
        await session.StoreAsync(new ShComment { Id = "ShComments/1", NoteId = note.Id, OrganizationId = acme.Id, Text = "first", Author = "ann" });
        await session.StoreAsync(new ShComment { Id = "ShComments/2", NoteId = note.Id, OrganizationId = acme.Id, Text = "second", Author = "max" });
        await session.StoreAsync(new ShComment { Id = "ShComments/3", NoteId = other.Id, OrganizationId = globex.Id, Text = "elsewhere", Author = "out" });
        await session.SaveChangesAsync();
        return (ann, max, out_, note, other);
    }

    private async Task<Link> ShareAsync(string noteId, string userId)
    {
        var (status, raw) = await ExecuteAsync("ShareLink", noteId, userId);
        status.Should().Be(HttpStatusCode.OK, raw);
        // The link travels once, in the showSecret client operation.
        var value = JsonDocument.Parse(raw).RootElement.EnumerateObject()
            .SelectMany(p => Flatten(p.Value)).First(s => s.Contains("/shared/"));
        var uri = new Uri(value);
        uri.GetLeftPart(UriPartial.Authority).Should().Be(PublicBaseUrl);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        segments[0].Should().Be("shared");
        return new Link(segments[1], Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)["id"].ToString(), Uri.UnescapeDataString(segments[2]));

        static IEnumerable<string> Flatten(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => [e.GetString()!],
            JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Flatten(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().SelectMany(Flatten),
            _ => [],
        };
    }

    private async Task<(HttpStatusCode Status, string Raw)> ExecuteAsync(string action, string noteId, string userId)
    {
        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/actions/execute")
        {
            Content = System.Net.Http.Json.JsonContent.Create(Wire.Action(NoteTypeId, action, new { parent = new { id = noteId, objectTypeId = NoteTypeId, name = "ShNote" } })),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> LoadSharedAsync(Link link)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsync("/spark/share/load", System.Net.Http.Json.JsonContent.Create(new { type = link.Type, id = link.Id, token = link.Token }));
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(raw) ? default : JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "ShNote",
            ClrType = typeof(ShNote).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "Secret", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "OrganizationId", DataType = "string" },
                // An application's mistake the framework survives: the hash as an editable attribute.
                new() { Id = Guid.NewGuid(), Name = "ShareTokenHash", DataType = "string" },
            ],
            Queries = ["ShNoteComments"],
        },
        Queries = [new SparkQuery { Id = Guid.Parse("05ae0000-0000-4000-8000-000000000010"), Name = "ShNotes", Source = "Custom.ShNotes", EntityType = "ShNote" }],
    };

    private static EntityTypeFile CommentModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CommentTypeId,
            Name = "ShComment",
            ClrType = typeof(ShComment).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "Author", DataType = "string" },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = CommentsQueryId, Name = "ShNoteComments", Source = "Custom.ShNoteComments", EntityType = "ShComment" },
            new SparkQuery { Id = AllCommentsQueryId, Name = "AllShComments", Source = "Custom.AllShComments", EntityType = "ShComment" },
        ],
    };

    private sealed class Gate(Func<bool> on) : ISparkShareGate
    {
        public ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken) => ValueTask.FromResult(on());
    }
}

[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole("RevokeShareLink", OrganizationRole.Admin)]
public class ShNote : IOrganizationOwned, ISparkShareable, IExpiring
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public string? OrganizationId { get; set; }
    public string? ShareTokenHash { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public class ShComment : IOrganizationOwned
{
    public string? Id { get; set; }
    public string? NoteId { get; set; }
    public string? OrganizationId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Author { get; set; }
}

public class ShNoteActions(IEntityMapper mapper, IAsyncDocumentSession session) : DefaultPersistentObjectActions<ShNote>(mapper)
{

    public IQueryable<ShNote> ShNotes() => session.Query<ShNote>();
}

public class ShCommentActions(IEntityMapper mapper, IAsyncDocumentSession session) : DefaultPersistentObjectActions<ShComment>(mapper)
{

    // Comments are organization-owned too: a share read reaches them only because it runs past that policy.
    public IQueryable<ShComment> ShNoteComments(CustomQueryArgs args)
        => args.Parent?.Id is { Length: > 0 } noteId
            ? session.Query<ShComment>().Where(c => c.NoteId == noteId)
            : Enumerable.Empty<ShComment>().AsQueryable();

    public IQueryable<ShComment> AllShComments() => session.Query<ShComment>();
}
