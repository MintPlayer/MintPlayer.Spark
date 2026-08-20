using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #281 — a row rule, or a redaction hook, over a <c>[FromIndex]</c>-projected entity. Both hooks are
/// declared on the entity and invoked reflectively, so the batched reload that correlates a projected
/// row back to its document must produce an instance of the entity type Spark <em>declares</em> — not
/// of whatever type the stored <c>@Raven-Clr-Type</c> happens to name.
/// <para>
/// That distinction is the whole bug. <c>LoadAsync&lt;object&gt;</c> usually looks fine: RavenDB reads
/// the CLR-type metadata and hands back a real entity, which is why every existing fixture — and the
/// Fleet demo, and the E2E row-level suite — pass over this code path today. It degrades to a
/// <c>JObject</c> only when that metadata is absent or unresolvable: documents written by a raw put,
/// bulk insert, Smuggler import or ETL, or an entity type since renamed or moved between assemblies.
/// Then the reflective invoke fails its argument check before a single row is judged, and the request
/// 500s. Loading as the declared type removes the dependency on document metadata altogether, so the
/// condition stops existing rather than merely becoming rarer.
/// </para>
/// </summary>
public class RowFilterProjectionReloadTests : SparkTestDriver
{
    private static readonly Guid LedgerTypeId = Guid.Parse("d1d1d1d1-8181-8181-8181-d1d1d1d1d1d1");

    /// <summary>A CLR type name no assembly in this process can resolve — the shape a document takes
    /// once its entity moved assembly, or once it was written by something that is not this app.</summary>
    private const string UnresolvableClrType = "Ghost.Ledger, Ghost";

    public class Ledger
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
    }

    /// <summary>Stands in for an index projection over <see cref="Ledger"/>: carries Id, omits Owner.</summary>
    public class VLedger
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
    }

    /// <summary>Both row hooks at once — the filter scopes rows to alice, the redaction hook hides
    /// every other row's secret. Its own static principal: fixture classes run concurrently.</summary>
    public class LedgerActions : DefaultPersistentObjectActions<Ledger>
    {
        public LedgerActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<Expression<Func<Ledger, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<Expression<Func<Ledger, bool>>?>(l => l.Owner == "alice");

        public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Ledger entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(entity.Owner == "alice" ? null : ["Secret"]);
    }

    private static (RowSecurity RowSecurity, EntityMapper Mapper) CreateSubjects()
    {
        var definition = new EntityTypeDefinition
        {
            Id = LedgerTypeId,
            Name = "Ledger",
            ClrType = typeof(Ledger).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Owner", DataType = "string", Order = 2 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Secret", DataType = "string", Order = 3 },
            ],
            Breadcrumb = "{Title}",
        };
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetEntityType(LedgerTypeId).Returns(definition);
        modelLoader.GetEntityTypeByClrType(typeof(Ledger).FullName!).Returns(definition);

        var mapper = new EntityMapper(modelLoader);
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Ledger)).Returns(new LedgerActions(mapper));
        return (new RowSecurity(actionsResolver), mapper);
    }

    /// <summary>Seeds alice's and bob's ledgers, optionally stamping a CLR type this process cannot
    /// resolve — the only difference between a working row filter and a 500.</summary>
    private async Task<(string AliceId, string BobId)> SeedAsync(bool clrTypeResolves)
    {
        using var session = Store.OpenAsyncSession();
        var alice = new Ledger { Title = "Alice's", Owner = "alice", Secret = "tok-a" };
        var bob = new Ledger { Title = "Bob's", Owner = "bob", Secret = "tok-b" };
        await session.StoreAsync(alice);
        await session.StoreAsync(bob);

        if (!clrTypeResolves)
        {
            session.Advanced.GetMetadataFor(alice)[Constants.Documents.Metadata.RavenClrType] = UnresolvableClrType;
            session.Advanced.GetMetadataFor(bob)[Constants.Documents.Metadata.RavenClrType] = UnresolvableClrType;
        }

        await session.SaveChangesAsync();
        return (alice.Id!, bob.Id!);
    }

    private static List<object> Projections(string aliceId, string bobId) =>
    [
        new VLedger { Id = aliceId, Title = "Alice's", Secret = "tok-a" },
        new VLedger { Id = bobId, Title = "Bob's", Secret = "tok-b" },
    ];

    // --- FilterAsync ------------------------------------------------------------------------

    /// <summary>The regression. Red before the fix with the production stack trace:
    /// <c>ArgumentException: Object of type 'JObject' cannot be converted to type 'Ledger'</c>.</summary>
    [Fact]
    public async Task A_projection_is_judged_as_the_entity_type_even_when_the_stored_clr_type_does_not_resolve()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: false);
        var (rowSecurity, _) = CreateSubjects();
        using var session = Store.OpenAsyncSession();

        var visible = await rowSecurity.FilterAsync(
            session, Projections(aliceId, bobId), typeof(Ledger), typeof(VLedger), "Query");

        visible.Should().ContainSingle().Which.Should().BeOfType<VLedger>()
            .Which.Title.Should().Be("Alice's");
    }

    /// <summary>Control: with resolvable metadata this already worked, because RavenDB recovered the
    /// type from the document. Pins that the fix does not regress the path every consumer is on, and
    /// that the reload is still a single batched request.</summary>
    [Fact]
    public async Task A_projection_is_judged_as_the_entity_type_when_the_stored_clr_type_resolves()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: true);
        var (rowSecurity, _) = CreateSubjects();
        using var session = Store.OpenAsyncSession();

        var visible = await rowSecurity.FilterAsync(
            session, Projections(aliceId, bobId), typeof(Ledger), typeof(VLedger), "Query");

        visible.Should().ContainSingle().Which.Should().BeOfType<VLedger>()
            .Which.Title.Should().Be("Alice's");
        session.Advanced.NumberOfRequests.Should().Be(1,
            "the page's base documents load in one batch — a per-row load would trip "
            + "MaxNumberOfRequestsPerSession past ~29 rows");
    }

    /// <summary>The index can still name a document that has since been deleted. Unverifiable, so the
    /// row is dropped — the typed load must not turn that into a throw.</summary>
    [Fact]
    public async Task A_projection_whose_base_document_was_deleted_is_dropped()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: false);
        using (var deleteSession = Store.OpenAsyncSession())
        {
            deleteSession.Delete(aliceId);
            await deleteSession.SaveChangesAsync();
        }

        var (rowSecurity, _) = CreateSubjects();
        using var session = Store.OpenAsyncSession();

        var visible = await rowSecurity.FilterAsync(
            session, Projections(aliceId, bobId), typeof(Ledger), typeof(VLedger), "Query");

        visible.Should().BeEmpty("alice's document is gone and bob's row fails the filter");
    }

    // --- RedactAsync ------------------------------------------------------------------------

    /// <summary>The twin defect the issue does not mention: <c>RedactAsync</c> repeats the untyped
    /// reload and feeds it to <c>GetProtectedAttributesAsync(string, TEntity)</c>, also invoked
    /// reflectively. Red before the fix.</summary>
    [Fact]
    public async Task Redaction_over_a_projection_reads_the_entity_type_when_the_stored_clr_type_does_not_resolve()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: false);
        var (rowSecurity, mapper) = CreateSubjects();

        var alicePo = mapper.ToPersistentObject(new Ledger { Id = aliceId, Title = "Alice's", Owner = "alice", Secret = "tok-a" }, LedgerTypeId);
        var bobPo = mapper.ToPersistentObject(new Ledger { Id = bobId, Title = "Bob's", Owner = "bob", Secret = "tok-b" }, LedgerTypeId);
        var rows = Projections(aliceId, bobId);

        using var session = Store.OpenAsyncSession();
        await rowSecurity.RedactAsync(
            session, [(alicePo, rows[0]), (bobPo, rows[1])], typeof(Ledger), typeof(VLedger), "Query");

        alicePo["Secret"].Value.Should().Be("tok-a", "the caller owns this row");
        bobPo["Secret"].Value.Should().BeNull(
            "the projection carries the secret, but the hook is asked against the base document");
        session.Advanced.NumberOfRequests.Should().Be(1);
    }

    // --- Session identity map (issue detail 1) ----------------------------------------------

    /// <summary>A server-side projection must not be tracked under the document id — if it were, the
    /// reload would get the <c>VLedger</c> back instead of the <c>Ledger</c> the rule needs.</summary>
    [Fact]
    public async Task A_projection_query_in_the_same_session_does_not_poison_the_typed_reload()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: false);
        var (rowSecurity, _) = CreateSubjects();
        using var session = Store.OpenAsyncSession();

        var projected = await session.Query<Ledger>()
            .Select(l => new VLedger { Id = l.Id, Title = l.Title, Secret = l.Secret })
            .ToListAsync();
        projected.Should().HaveCount(2, "the projection query touched exactly the ids the reload will ask for");

        var visible = await rowSecurity.FilterAsync(
            session, Projections(aliceId, bobId), typeof(Ledger), typeof(VLedger), "Query");

        visible.Should().ContainSingle().Which.Should().BeOfType<VLedger>()
            .Which.Title.Should().Be("Alice's");
    }

    /// <summary>The bonus the typed load buys: it primes the identity map with correctly-typed
    /// instances, so the untyped loads that run <em>after</em> it in a request — redaction, breadcrumb
    /// resolution — get the entity back rather than a <c>JObject</c> they would silently render as
    /// nothing. Order is what makes this safe, and this test is what pins the order.</summary>
    [Fact]
    public async Task The_typed_reload_primes_the_session_so_a_later_untyped_load_yields_the_entity_type()
    {
        var (aliceId, bobId) = await SeedAsync(clrTypeResolves: false);
        var (rowSecurity, _) = CreateSubjects();
        using var session = Store.OpenAsyncSession();

        await rowSecurity.FilterAsync(
            session, Projections(aliceId, bobId), typeof(Ledger), typeof(VLedger), "Query");

        (await session.LoadAsync<object>(aliceId)).Should().BeOfType<Ledger>();
        session.Advanced.NumberOfRequests.Should().Be(1, "served from the identity map, not a second request");
    }
}
