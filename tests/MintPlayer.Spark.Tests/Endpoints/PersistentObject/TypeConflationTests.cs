using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// The entity type a request is authorized against is the one the <b>server</b> resolved, never one
/// the client asserted inside the payload.
/// </summary>
/// <remarks>
/// <para>
/// The authoritative source is the request's own top-level <c>objectTypeId</c>, read in exactly one
/// place (<c>SparkRequestType.Resolve</c>). The payload's nested <c>objectTypeId</c> is overwritten
/// with what that resolves to, or ignored outright: <i>"taking the client's word for the type is how a
/// caller reads one collection through another's permissions"</i> — security sweep C3.
/// </para>
/// <para>
/// ⚠️ <b>This test exists because that source moved.</b> It was written against the old
/// <c>POST /spark/po/{objectTypeId}</c> routes, passed there, and passed unchanged afterwards — which
/// is what it was for. The route table is fully literal now (<c>POST /spark/po/create</c>) and the type
/// arrives as a top-level field in the request body. The safety property is the same — one
/// authoritative source, payload never trusted — but the distinction stopped being visual:
/// <c>request.ObjectTypeId</c> and <c>request.PersistentObject.ObjectTypeId</c> are one word apart in
/// the same document, where a route segment and a JSON body could not be confused.
/// </para>
/// <para>
/// Only the call sites changed in the migration — same types, same lies, same expected answers. If
/// someone wires authorization to the nested field, this is what fails. Do not rewrite it to match an
/// implementation; it is the thing the implementation has to satisfy.
/// </para>
/// </remarks>
public class TypeConflationTests : SparkTestDriver
{
    private static readonly Guid OpenTypeId = Guid.Parse("c04f0000-0000-4000-8000-c04f00000001");
    private static readonly Guid ClosedTypeId = Guid.Parse("c04f0000-0000-4000-8000-c04f00000002");

    /// <summary>A type the caller may write.</summary>
    public class ConflationOpen
    {
        public string? Id { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>A type the caller may not touch — every right is denied.</summary>
    public class ConflationClosed
    {
        public string? Id { get; set; }
        public string Secret { get; set; } = "";
    }

    private class ConflationContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<ConflationOpen> Opens => Session.Query<ConflationOpen>();
        public Raven.Client.Documents.Linq.IRavenQueryable<ConflationClosed> Closeds => Session.Query<ConflationClosed>();
    }

    public class ConflationOpenActions : DefaultPersistentObjectActions<ConflationOpen>, ISparkOwnsRowSecurity
    {
        public ConflationOpenActions(IEntityMapper mapper) : base(mapper) { }
        public string RowSecurityRationale => "Test fixture; every row is created by the test that reads it.";
    }

    public class ConflationClosedActions : DefaultPersistentObjectActions<ConflationClosed>, ISparkOwnsRowSecurity
    {
        public ConflationClosedActions(IEntityMapper mapper) : base(mapper) { }
        public string RowSecurityRationale => "Test fixture; the point is that it is unreachable.";
    }

    private SparkEndpointFactory<ConflationContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _factory = new SparkEndpointFactory<ConflationContext>(
            Store,
            [OpenModel(), ClosedModel()],
            configureServices: services =>
            {
                services.AddScoped<ConflationOpenActions>();
                services.AddScoped<ConflationClosedActions>();
            },
            // Everything allowed except ConflationClosed — so a caller reaching it at all is a finding,
            // not a fixture artefact.
            security: SparkTestSecurity.Permissive.Without("ConflationClosed"));

        _client = _factory.CreateClient();
        (_cookieHeader, _xsrfToken) = await _factory.MintAntiforgeryAsync();
    }

    public override async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Create_uses_the_server_resolved_type_not_the_payloads()
    {
        // Authoritative source says the permitted type; the payload claims the forbidden one.
        var (status, body) = await PostAsync(
            "/spark/po/create", Wire.Typed(OpenTypeId,
            new
            {
                persistentObject = new
                {
                    name = "ConflationOpen",
                    objectTypeId = ClosedTypeId.ToString(),   // ← the lie
                    attributes = new[] { new { name = "Label", value = "created", isValueChanged = true } },
                },
            }));

        status.Should().Be(HttpStatusCode.Created,
            "the authoritative type is permitted, so the write succeeds regardless of what the payload claims");

        body.GetProperty("result").GetProperty("objectTypeId").GetString()
            .Should().Be(OpenTypeId.ToString(),
                "the payload's objectTypeId must be overwritten with the server-resolved one, never honoured");
    }

    [Fact]
    public async Task Create_is_refused_when_the_server_resolved_type_is_forbidden()
    {
        // The mirror image, and the one that actually matters: claiming a permitted type in the
        // payload must not buy access to a forbidden one.
        var (status, _) = await PostAsync(
            "/spark/po/create", Wire.Typed(ClosedTypeId,
            new
            {
                persistentObject = new
                {
                    name = "ConflationClosed",
                    objectTypeId = OpenTypeId.ToString(),     // ← the lie, in the useful direction
                    attributes = new[] { new { name = "Secret", value = "leaked", isValueChanged = true } },
                },
            }));

        status.Should().NotBe(HttpStatusCode.Created,
            "a denied type must stay denied no matter which type the payload names");

        // A denial is deliberately indistinguishable from a missing type (M-3 oracle), so either
        // answer is correct here — what must never happen is a write.
        ((int)status is 403 or 404).Should().BeTrue(
            $"expected a refusal, got {(int)status}");
    }

    [Fact]
    public async Task Refresh_uses_the_server_resolved_type_not_the_payloads()
    {
        var open = new ConflationOpen { Label = "stored" };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(open);
            await session.SaveChangesAsync();
        }

        var (status, body) = await PostAsync(
            "/spark/po/refresh", Wire.Typed(OpenTypeId, new
            {
                persistentObject = new
                {
                    id = open.Id,
                    name = "ConflationOpen",
                    objectTypeId = ClosedTypeId.ToString(),   // ← the lie
                    attributes = new[] { new { name = "Label", value = "changed", isValueChanged = true } },
                },
                triggeredBy = "Label",
            }));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .Should().Contain("Label",
                "the reshaped object must follow the server-resolved type's schema, not the payload's claim");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static EntityTypeFile OpenModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = OpenTypeId,
            Name = "ConflationOpen",
            ClrType = typeof(ConflationOpen).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Label", DataType = "string", IsVisible = true },
            ],
        },
    };

    private static EntityTypeFile ClosedModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ClosedTypeId,
            Name = "ConflationClosed",
            ClrType = typeof(ConflationClosed).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Secret", DataType = "string", IsVisible = true },
            ],
        },
    };
}
