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

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

using Po = Abstractions.PersistentObject;

/// <summary>
/// <c>POST /spark/po/{type}/new</c> and <c>POST /spark/po/{type}/delete-row</c> against the real
/// route table, the real antiforgery gate, the real <c>security.json</c> enforcement and a real
/// RavenDB — everything except a browser.
/// </summary>
/// <remarks>
/// This is the layer the invoker unit tests cannot reach and the Fleet E2E reaches too slowly to
/// enumerate cases in. The invoker tests construct the args themselves, so they cannot see a route
/// that is not registered, a right checked against the wrong type name, or a stored row looked up
/// from the payload instead of the database. Those are the failures this suite exists for.
/// <para>
/// ⚠️ The refusal cases are the point, not the happy paths. Every one of them collapses to the same
/// answer a caller gets for a type that does not exist, so none of them is an oracle: "may I remove
/// row X" must not be a way to discover which rows, collections or parents are there.
/// </para>
/// </remarks>
public class RowLifecycleEndpointTests : SparkTestDriver
{
    private static readonly Guid InvoiceTypeId = Guid.Parse("9a1c0000-0000-4000-8000-9a1c00000001");
    private static readonly Guid LineTypeId = Guid.Parse("9a1c0000-0000-4000-8000-9a1c00000002");

    public class Invoice
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = string.Empty;
        public List<InvoiceLine> Lines { get; set; } = [];
    }

    /// <summary>Keyed the way <c>ValueObjectKeyGenerator</c> keys a <c>[ValueObject]</c>.</summary>
    public class InvoiceLine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Description { get; set; } = string.Empty;

        /// <summary>A C# initializer, so it must arrive as the new row's default.</summary>
        public int Quantity { get; set; } = 1;

        /// <summary>Once true the line is an accounting record and the hook refuses to release it.</summary>
        public bool IsSettled { get; set; }
    }

    private class InvoiceContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<Invoice> Invoices => Session.Query<Invoice>();
    }

    static RowLifecycleEndpointTests()
        => SparkValueObjects.Register(typeof(InvoiceLine), "Id", row => ((InvoiceLine)row).Id);

    /// <summary>
    /// The parent's actions class. Exists only to carry the row-security declaration.
    /// </summary>
    /// <remarks>
    /// Once <c>security.json</c> states explicit grants rather than the permissive wildcard, Spark's
    /// startup gate refuses a type granted to <c>anonymous</c> that declares no row rule — publishing
    /// a whole collection is a decision an application may make, but not one it may leave
    /// undecided. A fixture is exactly the place that rule is meant to be answered rather than
    /// worked around, so it is answered.
    /// </remarks>
    public class InvoiceActions : DefaultPersistentObjectActions<Invoice>, ISparkOwnsRowSecurity
    {
        public InvoiceActions(IEntityMapper mapper) : base(mapper) { }

        public string RowSecurityRationale =>
            "Test fixture. Every invoice in this database is created by the test that reads it, so "
            + "there is no other caller's data to scope away.";
    }

    /// <summary>Overrides both hooks: defaults a new line, refuses to release a settled one.</summary>
    public class InvoiceLineActions : DefaultPersistentObjectActions<InvoiceLine>, ISparkOwnsRowSecurity
    {
        public InvoiceLineActions(IEntityMapper mapper) : base(mapper) { }

        public string RowSecurityRationale =>
            "Test fixture. Lines are embedded in Invoice and reachable only through it, so they are "
            + "scoped by whatever scopes the parent.";

        public override Task OnNewAsync(SparkNewArgs<InvoiceLine> args)
        {
            args.PersistentObject["Description"].SetOriginalValue(
                $"Line for {args.AsDetailParent?.Attributes.FirstOrDefault(a => a.Name == "Reference")?.Value}");
            return Task.CompletedTask;
        }

        public override Task OnDeleteRowAsync(SparkDeleteRowArgs<InvoiceLine> args)
        {
            var settled = args.Row.Attributes.FirstOrDefault(a => a.Name == "IsSettled")?.Value;
            if (settled is true || string.Equals(settled?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                throw new SparkValidationException("This line has been settled.", args.AsDetailAttribute);

            return Task.CompletedTask;
        }
    }

    private static EntityTypeFile InvoiceModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = InvoiceTypeId,
            Name = "Invoice",
            ClrType = typeof(Invoice).FullName!,
            Breadcrumb = "{Reference}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Reference", DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail",
                    AsDetailType = typeof(InvoiceLine).FullName, IsArray = true,
                },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "InvoiceLine",
            ClrType = typeof(InvoiceLine).FullName!,
            Breadcrumb = "{Description}",
            // The opt-in. Note the endpoints do NOT read it — it tells the client to ask. It is set
            // here so the fixture matches how a real application declares the feature.
            ServerSideRowLifecycle = true,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Description", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Quantity", DataType = "number" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "IsSettled", DataType = "boolean" },
            ],
        },
    };

    private SparkEndpointFactory<InvoiceContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await ArmAsync(SparkTestSecurity.Permissive);
    }

    /// <summary>
    /// Rebuilds the host under a given security posture. A method rather than constructor work
    /// because half these tests are about the rights and need a different one.
    /// </summary>
    private async Task ArmAsync(SparkTestSecurity security)
    {
        if (_factory is not null)
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        _factory = new SparkEndpointFactory<InvoiceContext>(
            Store,
            [InvoiceModel(), LineModel()],
            configureServices: services =>
            {
                services.AddScoped<InvoiceActions>();
                services.AddScoped<InvoiceLineActions>();
            },
            security: security);

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

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object payload, bool withToken = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", _cookieHeader);
        if (withToken)
            request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Stores an invoice with one settled line and one open one, and returns its id.</summary>
    private async Task<Invoice> SeedAsync()
    {
        var invoice = new Invoice
        {
            Reference = "INV-1",
            Lines =
            [
                new InvoiceLine { Description = "Consultancy", Quantity = 3 },
                new InvoiceLine { Description = "Licence", Quantity = 1, IsSettled = true },
            ],
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(invoice);
        await session.SaveChangesAsync();
        return invoice;
    }

    private static JsonElement Attribute(JsonElement body, string name) =>
        body.GetProperty("result").GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    private object NewBody(string invoiceId) => new
    {
        asDetailAttribute = "Lines",
        parentType = InvoiceTypeId.ToString(),
        parentId = invoiceId,
    };

    private object DeleteBody(string invoiceId, string rowKey) => new
    {
        asDetailAttribute = "Lines",
        parentType = InvoiceTypeId.ToString(),
        parentId = invoiceId,
        rowKey,
    };

    // ---- New ----------------------------------------------------------------------------------

    [Fact]
    public async Task New_returns_a_row_carrying_its_key()
    {
        var invoice = await SeedAsync();

        var (status, body) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody(invoice.Id!));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace(
            "the client sends this back as __sparkRowKey, and a save can only match a row that has one");
    }

    [Fact]
    public async Task New_applies_the_hook_and_the_C_sharp_initializer()
    {
        var invoice = await SeedAsync();

        var (_, body) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody(invoice.Id!));

        Attribute(body, "Description").GetProperty("value").GetString()
            .Should().Be("Line for INV-1", "the hook read the parent the server loaded");
        Attribute(body, "Quantity").GetProperty("value").GetInt32()
            .Should().Be(1, "constructing the entity is what makes a property initializer a default");
    }

    [Fact]
    public async Task New_defaults_do_not_arrive_marked_changed()
    {
        var invoice = await SeedAsync();

        var (_, body) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody(invoice.Id!));

        Attribute(body, "Description").GetProperty("isValueChanged").GetBoolean().Should().BeFalse(
            "SetOriginalValue, not SetValue — otherwise adding a row and abandoning it leaves the "
            + "parent falsely modified");
    }

    [Fact]
    public async Task New_without_a_parent_id_still_constructs_a_row()
    {
        // The unsaved-parent case: there is no stored parent to vouch for, so the hook is handed
        // null rather than the client's copy. Constructing the row must still work.
        var (status, body) = await PostAsync($"/spark/po/{LineTypeId}/new", new
        {
            asDetailAttribute = "Lines",
            parentType = InvoiceTypeId.ToString(),
        });

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "Description").GetProperty("value").GetString().Should().Be("Line for ");
    }

    [Fact]
    public async Task New_is_refused_when_the_route_type_is_not_the_parent_s_declared_child()
    {
        var invoice = await SeedAsync();

        // Naming the parent type as its own child. Without the schema check a caller could have any
        // type at all constructed under a parent that has no such collection.
        var (status, _) = await PostAsync($"/spark/po/{InvoiceTypeId}/new", NewBody(invoice.Id!));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_is_refused_for_an_attribute_the_parent_does_not_have()
    {
        var invoice = await SeedAsync();

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/new", new
        {
            asDetailAttribute = "Attachments",
            parentType = InvoiceTypeId.ToString(),
            parentId = invoice.Id,
        });

        // Identical to an unknown type, so this cannot answer "does that collection exist".
        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_is_refused_for_a_parent_that_does_not_exist()
    {
        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody("Invoices/does-not-exist"));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_requires_an_antiforgery_token()
    {
        var invoice = await SeedAsync();

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody(invoice.Id!), withToken: false);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task New_needs_the_row_type_s_own_right_not_the_parent_s()
    {
        var invoice = await SeedAsync();

        // Everything on Invoice, nothing that lets a caller create a Line. The distinction the whole
        // authorization design rests on: the grid's affordances are governed by the type in the grid.
        await ArmAsync(SparkTestSecurity.Empty.Granting(
            "QueryReadEditNewDelete/Invoice", "QueryRead/InvoiceLine"));

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/new", NewBody(invoice.Id!));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_row_allows_an_open_row()
    {
        var invoice = await SeedAsync();
        var openKey = invoice.Lines.Single(l => !l.IsSettled).Id;

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, openKey));

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_row_writes_nothing()
    {
        var invoice = await SeedAsync();
        var openKey = invoice.Lines.Single(l => !l.IsSettled).Id;

        await PostAsync($"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, openKey));

        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Invoice>(invoice.Id!);

        // Consultation, not deletion. The row leaves the database when the PARENT is saved; an
        // endpoint that deleted here would be writing outside the parent's unit of work, and a user
        // who then cancelled the form would have lost the row anyway.
        stored.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_row_surfaces_a_hook_s_refusal_as_a_readable_error()
    {
        var invoice = await SeedAsync();
        var settledKey = invoice.Lines.Single(l => l.IsSettled).Id;

        var (status, body) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, settledKey));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("result").GetProperty("errors")[0]
            .GetProperty("errorMessage").GetProperty("en").GetString()
            .Should().Be("This line has been settled.");
    }

    [Fact]
    public async Task Delete_row_judges_the_stored_row_not_the_caller_s_claim_about_it()
    {
        var invoice = await SeedAsync();
        var settledKey = invoice.Lines.Single(l => l.IsSettled).Id;

        // The attack: the caller asserts the row is not settled. The endpoint never reads a row from
        // the body, so the assertion is simply ignored — had it not been, the refusal would be
        // consulting the very claim it exists to doubt.
        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", new
        {
            asDetailAttribute = "Lines",
            parentType = InvoiceTypeId.ToString(),
            parentId = invoice.Id,
            rowKey = settledKey,
            persistentObject = new
            {
                name = "InvoiceLine",
                attributes = new object[] { new { name = "IsSettled", value = false } },
            },
        });

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_row_is_refused_for_a_key_the_parent_does_not_hold()
    {
        var invoice = await SeedAsync();

        var (status, _) = await PostAsync(
            $"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, Guid.NewGuid().ToString("N")));

        // Refused identically to an unknown type, so the endpoint cannot be used to enumerate keys.
        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_row_is_refused_without_a_parent_id()
    {
        // Unlike New, a missing parent id is not a legitimate case here: an unsaved parent has no
        // stored rows, so there is nothing to consult and the client does not ask.
        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", new
        {
            asDetailAttribute = "Lines",
            parentType = InvoiceTypeId.ToString(),
            rowKey = "anything",
        });

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_row_requires_an_antiforgery_token()
    {
        var invoice = await SeedAsync();
        var openKey = invoice.Lines.Single(l => !l.IsSettled).Id;

        var (status, _) = await PostAsync(
            $"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, openKey), withToken: false);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_row_needs_the_row_type_s_own_Delete_right()
    {
        var invoice = await SeedAsync();
        var openKey = invoice.Lines.Single(l => !l.IsSettled).Id;

        // Edit on the row type, but not Delete. A deployment may well want exactly this: lines
        // correctable by many, removable by few.
        await ArmAsync(SparkTestSecurity.Empty.Granting(
            "QueryReadEditNewDelete/Invoice", "QueryReadEditNew/InvoiceLine"));

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, openKey));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_parent_cannot_ask_about_its_rows()
    {
        var invoice = await SeedAsync();
        var openKey = invoice.Lines.Single(l => !l.IsSettled).Id;

        // Full rights on the row type, none on the parent. The row type's right says the caller may
        // remove rows of this kind; it does not say which parent's. The parent load is the second
        // gate, and without it this would be a way to confirm an invoice exists.
        await ArmAsync(SparkTestSecurity.Empty.Granting("QueryReadEditNewDelete/InvoiceLine"));

        var (status, _) = await PostAsync($"/spark/po/{LineTypeId}/delete-row", DeleteBody(invoice.Id!, openKey));

        status.Should().Be(HttpStatusCode.NotFound);
    }
}
