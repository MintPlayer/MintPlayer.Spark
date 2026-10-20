using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

using Po = Abstractions.PersistentObject;

// Fixture names start with "Sr": ActionsResolver matches actions classes by simple name across the whole assembly.
public class SrInvoice
{
    public string? Id { get; set; }
    public string Reference { get; set; } = "";
    public List<SrLine> Lines { get; set; } = [];
}

/// <summary>An AsDetail row, keyed (registered in the test's static ctor) so an edit merges onto the stored row.</summary>
public class SrLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>What the test replacement records instead of the save: the stand-in for the Approvals library's proposal.</summary>
public class SrProposal
{
    public string? Id { get; set; }
    public Guid ObjectTypeId { get; set; }
    /// <summary>The wire JSON text, never the object (S10: a RavenDB field loses the AsDetail type split).</summary>
    public string Json { get; set; } = "";
    public string? BaseEtag { get; set; }
    public string Status { get; set; } = "Pending";
}

public class SrInvoiceActions(IEntityMapper mapper) : DefaultPersistentObjectActions<SrInvoice>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is the save-replacement seam.";
}

public class SrLineActions(IEntityMapper mapper) : DefaultPersistentObjectActions<SrLine>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: lines are embedded in SrInvoice and scoped by it.";
}

public class SrContext : SparkContext
{
    public IRavenQueryable<SrInvoice> Invoices => Session.Query<SrInvoice>();
}

/// <summary>How often the replacement was asked, and every before-save the interceptors saw.</summary>
public sealed class SrLog
{
    public int Asked;
    public List<(bool IsApply, string? OnBehalfOf, string? Fact)> Saves { get; } = [];
}

/// <summary>Diverts every save of an <see cref="SrInvoice"/> into an <see cref="SrProposal"/>.</summary>
public sealed class SrProposalReplacement(SrLog log) : ISaveReplacement
{
    public bool AppliesTo(Type entityType) => entityType == typeof(SrInvoice);

    public async ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context)
    {
        Interlocked.Increment(ref log.Asked);
        var proposal = new SrProposal
        {
            ObjectTypeId = context.PersistentObject.ObjectTypeId,
            Json = context.PersistentObjectJson,
            BaseEtag = context.BaseEtag,
        };
        await ((IAsyncDocumentSession)context.Session).StoreAsync(proposal);
        return new SaveReplacementReceipt("approval", proposal.Id!);
    }
}

/// <summary>A second replacement for the same type: refused, at most one may govern it.</summary>
public sealed class SrSecondReplacement : ISaveReplacement
{
    public bool AppliesTo(Type entityType) => entityType == typeof(SrInvoice);

    public ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context) => ValueTask.FromResult<SaveReplacementReceipt?>(null);
}

public sealed class SrRecordingInterceptor(SrLog log) : IBeforeSave
{
    public bool AppliesTo(Type entityType) => entityType == typeof(SrInvoice);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        context.Facts.TryGetValue(SparkFacts.OnBehalfOf, out var fact);
        log.Saves.Add((context.IsReplacedSaveApply, context.OnBehalfOf, fact));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Commerce X7 (S10): <see cref="ISaveReplacement"/> diverts a save after every gate and before anything is mapped,
/// answering 202 with a receipt; <see cref="ISparkReplacedSaves"/> applies the stored wire JSON later as the approver
/// with <c>Etag = base</c>. The test replacement stands in for the Approvals library's document proposals.
/// </summary>
public class SaveReplacementTests : SparkTestDriver
{
    private static readonly Guid InvoiceTypeId = Guid.Parse("5a7e0000-0000-4000-8000-5a7e00000001");
    private static readonly Guid LineTypeId = Guid.Parse("5a7e0000-0000-4000-8000-5a7e00000002");
    private const string InvoiceId = "srinvoices/1";
    private const string Proposer = "users/proposer";

    static SaveReplacementTests()
        => SparkValueObjects.Register(typeof(SrLine), "Id", row => ((SrLine)row).Id);

    private readonly SrLog log = new();
    private readonly List<IAsyncDisposable> factories = [];
    private readonly List<SparkClient> clients = [];

    private static EntityTypeFile InvoiceModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = InvoiceTypeId,
            Name = "SrInvoice",
            ClrType = typeof(SrInvoice).FullName!,
            Breadcrumb = "{Reference}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", Order = 1 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail",
                    AsDetailType = typeof(SrLine).FullName, IsArray = true, Order = 2,
                },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "SrLine",
            ClrType = typeof(SrLine).FullName!,
            Breadcrumb = "{Description}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Description", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Quantity", DataType = "number", Order = 2 },
            ],
        },
    };

    private async Task<(SparkEndpointFactory<SrContext> Factory, SparkClient Client)> StartAsync(bool twoReplacements = false)
    {
        await SeedAsync(session => session.StoreAsync(new SrInvoice
        {
            Reference = "INV-1",
            Lines =
            [
                new SrLine { Id = "l1", Description = "Consultancy", Quantity = 3 },
                new SrLine { Id = "l2", Description = "Licence", Quantity = 1 },
            ],
        }, InvoiceId));

        var factory = new SparkEndpointFactory<SrContext>(
            Store,
            [InvoiceModel(), LineModel()],
            configureServices: services =>
            {
                services.AddSingleton(log);
                services.AddScoped<SrInvoiceActions>();
                services.AddScoped<SrLineActions>();
            },
            configureSpark: spark =>
            {
                spark.AddInterceptor<SrProposalReplacement>();
                spark.AddInterceptor<SrRecordingInterceptor>();
                if (twoReplacements)
                    spark.AddInterceptor<SrSecondReplacement>();
            });
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);
        return (factory, client);
    }

    public override async Task DisposeAsync()
    {
        foreach (var client in clients)
            client.Dispose();
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static void Set(PersistentObjectAttribute attribute, object? value)
    {
        attribute.Value = value;
        attribute.IsValueChanged = true;
    }

    /// <summary>Posts an edit of the reference and the first line over HTTP; the replacement diverts it.</summary>
    private async Task<SrProposal> ProposeEditAsync(SparkClient client)
    {
        var po = await client.GetPersistentObjectAsync(InvoiceTypeId, InvoiceId);
        Set(po!["Reference"], "INV-1b");
        var lines = (PersistentObjectAttributeAsDetail)po["Lines"];
        Set(lines.Objects![0]["Description"], "Consultancy (revised)");
        lines.IsValueChanged = true;

        var ex = (await new Func<Task>(() => client.UpdatePersistentObjectAsync(po)).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.Accepted, "a diverted save is submitted, not refused");
        using var receipt = JsonDocument.Parse(ex.ResponseBody!);
        var submitted = receipt.RootElement.GetProperty("submitted");
        submitted.GetProperty("kind").GetString().Should().Be("approval");
        return await LoadProposalAsync(submitted.GetProperty("referenceId").GetString()!);
    }

    private async Task<SrProposal> LoadProposalAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<SrProposal>(id))!;
    }

    private async Task<(SrInvoice Invoice, string ChangeVector)> LoadInvoiceAsync(string id = InvoiceId)
    {
        using var session = Store.OpenAsyncSession();
        var invoice = await session.LoadAsync<SrInvoice>(id);
        return (invoice, session.Advanced.GetChangeVectorFor(invoice));
    }

    private static ReplacedSave Apply(SrProposal proposal) => new()
    {
        ObjectTypeId = proposal.ObjectTypeId,
        PersistentObjectJson = proposal.Json,
        BaseEtag = proposal.BaseEtag,
        OnBehalfOf = Proposer,
        Alongside = async session =>
        {
            var stored = await ((IAsyncDocumentSession)session).LoadAsync<SrProposal>(proposal.Id);
            stored.Status = "Applied";
        },
    };

    [Fact]
    public async Task A_diverted_update_answers_202_writes_nothing_and_keeps_the_nested_rows_as_wire_json()
    {
        var (_, client) = await StartAsync();
        var (_, baseVector) = await LoadInvoiceAsync();

        var proposal = await ProposeEditAsync(client);

        var (stored, vector) = await LoadInvoiceAsync();
        stored.Reference.Should().Be("INV-1", "nothing of a diverted save is written");
        stored.Lines[0].Description.Should().Be("Consultancy");
        vector.Should().Be(baseVector);
        proposal.BaseEtag.Should().Be(baseVector, "the base is the version the etag check verified");
        log.Asked.Should().Be(1);
        log.Saves.Should().BeEmpty("no before-save interceptor runs for a diverted save; they run once, at apply");

        // The AsDetail type split survives: the rows come back as rows, with the edited value.
        var proposed = SparkReplacedSaves.Deserialize(proposal.Json);
        var lines = proposed["Lines"].Should().BeOfType<PersistentObjectAttributeAsDetail>().Which;
        lines.Objects.Should().HaveCount(2);
        lines.Objects![0]["Description"].Value?.ToString().Should().Be("Consultancy (revised)");
        proposed["Reference"].Value?.ToString().Should().Be("INV-1b");
    }

    [Fact]
    public async Task Applying_as_the_approver_writes_the_change_with_the_base_etag_in_one_commit_with_the_proposal()
    {
        var (factory, client) = await StartAsync();
        var proposal = await ProposeEditAsync(client);

        using var scope = factory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISparkReplacedSaves>().ApplyAsync(Apply(proposal));

        outcome.Kind.Should().Be(ReplacedSaveOutcomeKind.Applied);
        var (stored, _) = await LoadInvoiceAsync();
        stored.Reference.Should().Be("INV-1b");
        stored.Lines.Select(l => (l.Id, l.Description, l.Quantity)).Should().Equal(
            [("l1", "Consultancy (revised)", 3), ("l2", "Licence", 1)]);
        (await LoadProposalAsync(proposal.Id!)).Status.Should().Be("Applied", "the applier's own write commits with the apply");

        log.Asked.Should().Be(1, "the apply does not ask the replacement again: an approved proposal cannot propose itself");
        log.Saves.Should().ContainSingle().Which.Should().Be((true, Proposer, Proposer));
    }

    [Fact]
    public async Task A_stale_base_is_a_conflict_and_writes_nothing()
    {
        var (factory, client) = await StartAsync();
        var proposal = await ProposeEditAsync(client);

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<SrInvoice>(InvoiceId)).Reference = "someone else";
            await session.SaveChangesAsync();
        }

        using var scope = factory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISparkReplacedSaves>().ApplyAsync(Apply(proposal));

        outcome.Kind.Should().Be(ReplacedSaveOutcomeKind.Conflict, "the row changed since the proposal: a 409, the proposal is stale");
        outcome.ConflictReason.Should().Be("changed");
        var (stored, _) = await LoadInvoiceAsync();
        stored.Reference.Should().Be("someone else");
        stored.Lines[0].Description.Should().Be("Consultancy");
        (await LoadProposalAsync(proposal.Id!)).Status.Should().Be("Pending", "nothing of the apply commits");
        log.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task A_diverted_create_with_rows_is_applied_as_a_new_document()
    {
        var (factory, client) = await StartAsync();
        var po = await client.NewPersistentObjectAsync(InvoiceTypeId);
        Set(po["Reference"], "INV-2");
        var lines = (PersistentObjectAttributeAsDetail)po["Lines"];
        lines.Objects =
        [
            new Po
            {
                Name = "SrLine",
                ObjectTypeId = LineTypeId,
                Attributes =
                [
                    new PersistentObjectAttribute { Name = "Description", DataType = "string", Value = "Setup", IsValueChanged = true },
                    new PersistentObjectAttribute { Name = "Quantity", DataType = "number", Value = 2, IsValueChanged = true },
                ],
            },
        ];
        lines.IsValueChanged = true;

        var ex = (await new Func<Task>(() => client.CreatePersistentObjectAsync(po)).Should().ThrowExactlyAsync<SparkClientException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var receipt = JsonDocument.Parse(ex.ResponseBody!);
        var proposal = await LoadProposalAsync(receipt.RootElement.GetProperty("submitted").GetProperty("referenceId").GetString()!);
        proposal.BaseEtag.Should().BeNull("a creation has no base");
        using (var session = Store.OpenAsyncSession())
            (await session.Query<SrInvoice>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(1, "nothing was created");

        using var scope = factory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ISparkReplacedSaves>().ApplyAsync(Apply(proposal));

        outcome.Kind.Should().Be(ReplacedSaveOutcomeKind.Applied);
        var (created, _) = await LoadInvoiceAsync(outcome.Object!.Id!);
        created.Reference.Should().Be("INV-2");
        created.Lines.Select(l => (l.Description, l.Quantity)).Should().Equal([("Setup", 2)]);
    }

    [Fact]
    public async Task At_most_one_save_replacement_may_govern_a_type()
    {
        var (factory, _) = await StartAsync(twoReplacements: true);

        using var scope = factory.CreateScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = await databaseAccess.GetPersistentObjectAsync(InvoiceTypeId, InvoiceId);
        po!["Reference"].Value = "INV-1c";

        var ex = await Record.ExceptionAsync(() => databaseAccess.SavePersistentObjectAsync(po));

        ex.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("More than one ISaveReplacement");
        (await LoadInvoiceAsync()).Invoice.Reference.Should().Be("INV-1");
    }

    [Fact]
    public void The_wire_form_keeps_the_AsDetail_split_and_names_every_row_attribute()
    {
        var po = new Po
        {
            Name = "SrInvoice",
            ObjectTypeId = InvoiceTypeId,
            Id = InvoiceId,
            Etag = "A:1-x",
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Reference", Value = "INV-1" },
                new PersistentObjectAttributeAsDetail
                {
                    Name = "Lines", DataType = "AsDetail", IsArray = true, AsDetailType = typeof(SrLine).FullName,
                    Objects =
                    [
                        new Po { Name = "SrLine", ObjectTypeId = LineTypeId, Attributes = [new PersistentObjectAttribute { Name = "Description", Value = "a" }] },
                        new Po { Name = "SrLine", ObjectTypeId = LineTypeId, Attributes = [new PersistentObjectAttribute { Name = "Quantity", DataType = "number", Value = 2 }] },
                    ],
                },
            ],
        };

        var back = SparkReplacedSaves.Deserialize(SparkReplacedSaves.Serialize(po));

        back.Id.Should().Be(InvoiceId);
        back.Etag.Should().Be("A:1-x");
        var lines = back["Lines"].Should().BeOfType<PersistentObjectAttributeAsDetail>().Which;
        lines.Objects![0]["Description"].Value?.ToString().Should().Be("a");
        lines.Objects[1]["Quantity"].Value?.ToString().Should().Be("2");
        SparkReplacedSaves.AttributePaths(back).Should().BeEquivalentTo(["Reference", "Lines", "Lines.Description", "Lines.Quantity"]);
    }
}
