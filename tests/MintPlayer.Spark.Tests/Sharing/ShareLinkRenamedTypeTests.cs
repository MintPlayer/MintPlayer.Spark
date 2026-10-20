using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Sharing;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Sharing;

/// <summary>
/// D28: a shareable type whose persistent-object name is not its CLR class's name (an application rename, or a model
/// file named otherwise). The registration used to be keyed by <c>typeof(T).Name</c> (<c>SparkShareOptions.cs:36</c>)
/// and the model type looked up by that name, so making a link answered "sharing is switched off" and no link read.
/// </summary>
public class ShareLinkRenamedTypeTests : OrganizationsTestHost
{
    private static readonly Guid NoteTypeId = Guid.Parse("05ae0000-0000-4000-8000-000000000101");

    public ShareLinkRenamedTypeTests()
    {
        ExtraModels.Add(new EntityTypeFile
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = NoteTypeId,
                Name = "PublicNote",
                ClrType = typeof(ShNote).FullName!,
                Attributes =
                [
                    new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                    new() { Id = Guid.NewGuid(), Name = "OrganizationId", DataType = "string" },
                ],
            },
        });
        ExtraRights.Add(new Right { Key = "pn-notes", Resource = "QueryReadEditNewDelete/PublicNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraRights.Add(new Right { Key = "pn-share", Resource = "ShareLink/PublicNote", GroupId = SparkTestSecurity.AuthenticatedGroupId });
        ExtraServices = services => services.AddScoped<ShNoteActions>();
        ExtraSpark = spark => spark.AddShareLinks(o =>
        {
            o.RequestsPerMinute = 1000;
            o.PublicBaseUrl = PublicBaseUrl;
            o.Share<ShNote>(attributes: ["Title"]);
        });
    }

    [Fact]
    public async Task A_type_named_otherwise_than_its_class_is_shared_and_read()
    {
        var ann = await SeedUserAsync("ann");
        var acme = await SeedOrganizationAsync("Acme", ann);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ShNote { Id = "ShNotes/1", Title = "Launch plan", Secret = "budget", OrganizationId = acme.Id });
            await session.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, ann.Id);
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/actions/execute")
        {
            Content = System.Net.Http.Json.JsonContent.Create(Wire.Action(NoteTypeId, "ShareLink", new { parent = new { id = "ShNotes/1", objectTypeId = NoteTypeId, name = "PublicNote" } })),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        request.Headers.Add(OgPrincipalStartupFilter.Header, ann.Id!);
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        raw.Should().NotContain("Sharing is switched off");
        var url = new Uri(JsonDocument.Parse(raw).RootElement.EnumerateObject().SelectMany(p => Strings(p.Value)).First(s => s.Contains("/shared/")));
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        segments[1].Should().Be(NoteTypeId.ToString("D"));

        foreach (var type in new[] { segments[1], "PublicNote", "ShNote" })
        {
            using var reader = Factory.CreateClient();
            var load = await reader.PostAsync("/spark/share/load", System.Net.Http.Json.JsonContent.Create(new
            {
                type,
                id = "ShNotes/1",
                token = Uri.UnescapeDataString(segments[2]),
            }));
            var body = await load.Content.ReadAsStringAsync();
            load.StatusCode.Should().Be(HttpStatusCode.OK, $"'{type}' names the shared type: {body}");
            body.Should().Contain("Launch plan").And.NotContain("budget");
        }
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}
