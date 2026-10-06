using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// Top-level, not nested in the test class: endpoints derive the right's type name from the CLR name's
// last dotted segment, which for a nested class is "Outer+Inner" and matches no right.
public class AttrKeeper
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
}

public class AttrVault
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Note { get; set; } = "";
    public string Pin { get; set; } = "";
    public bool Locked { get; set; }
    public string? Keeper { get; set; }
}

/// <summary>
/// Read-side enforcement of attribute-level rights (contributions M2c-2a, PRD §5 Q14) and the
/// read-path leaks the audit found, end to end over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The caller holds <c>QueryReadEditNew</c> on both types and is denied <c>QueryRead</c> on
/// <c>AttrVault.Secret</c> and on <c>AttrKeeper.Code</c>, and <c>Edit</c> on <c>AttrVault.Note</c>.
/// <c>Note</c> is also off every surface (<c>ShowedOn</c> none) and <c>Pin</c> is shown on the
/// detail page only and protected per row by the actions class while <c>Locked</c>.
/// </para>
/// <para>
/// Every value a test searches for, sorts by or filters on is unique to one attribute of one row, so
/// a match can only come from the attribute under test.
/// </para>
/// <para>
/// Names start with <c>Attr</c> because <c>ActionsResolver</c> matches actions classes by simple
/// name across the whole assembly.
/// </para>
/// </remarks>
public class AttributeRightsEnforcementTests(AttributeRightsEnforcementTests.Host host)
    : SparkSharedTestDriver(host), IClassFixture<AttributeRightsEnforcementTests.Host>, IDisposable
{
    private static readonly Guid VaultTypeId = Guid.Parse("a77a0000-0000-4000-8000-00000000a001");
    private static readonly Guid KeeperTypeId = Guid.Parse("a77a0000-0000-4000-8000-00000000a002");
    private static readonly Guid VaultsQueryId = Guid.Parse("a77a0000-0000-4000-8000-00000000a003");

    private const string Echo = "AttrVaultEcho";

    public sealed class AttrContext : SparkContext
    {
        public IRavenQueryable<AttrVault> Vaults => Session.Query<AttrVault>();
        public IRavenQueryable<AttrKeeper> Keepers => Session.Query<AttrKeeper>();
    }

    /// <summary>The per-row hook: a locked vault's Pin is protected.</summary>
    public class AttrVaultActions(IEntityMapper mapper)
        : DefaultPersistentObjectActions<AttrVault>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
    {
        public string RowSecurityRationale => "Test fixture: every row is visible; the subject is attribute rights.";

        public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, AttrVault entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(entity.Locked ? ["Pin"] : null);
    }

    public class AttrKeeperActions(IEntityMapper mapper)
        : DefaultPersistentObjectActions<AttrKeeper>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
    {
        public string RowSecurityRationale => "Test fixture: every row is visible; the subject is attribute rights.";
    }

    /// <summary>Returns the vault it ran on, loaded server side — a PO in an action's result.</summary>
    public sealed class AttrVaultEchoAction(IDatabaseAccess databaseAccess) : ICustomAction
    {
        public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
            => args.SetResult(await databaseAccess.GetPersistentObjectAsync(VaultTypeId, "vaults/1"));
    }

    /// <summary>
    /// One host and one seed for the class (M8 item 5). Every case seeded the same keeper and three
    /// vaults and then only read — searches, filters, loads, the echo action, the schema — so the
    /// seed moved to class setup and the cases share it.
    /// </summary>
    public sealed class Host : SharedSparkHost<AttrContext>
    {
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();

            using var session = Store.OpenAsyncSession();
            session.Advanced.WaitForIndexesAfterSaveChanges(RavenIndexingExtensions.DefaultTimeout, throwOnTimeout: true);
            await session.StoreAsync(new AttrKeeper { Name = "Kay", Code = "kcode777" }, "keepers/1");
            await session.StoreAsync(new AttrVault { Name = "Anna", Secret = "xbeta", Note = "hiddennote", Pin = "9911", Locked = true, Keeper = "keepers/1" }, "vaults/1");
            await session.StoreAsync(new AttrVault { Name = "Bert", Secret = "xgamma", Pin = "4321" }, "vaults/2");
            await session.StoreAsync(new AttrVault { Name = "Cleo", Secret = "xalpha" }, "vaults/3");
            await session.SaveChangesAsync();
        }

        protected override SparkEndpointFactory<AttrContext> CreateFactory() => new(
            Store,
            [VaultModel(), KeeperModel()],
            configureServices: services =>
            {
                services.AddScoped<AttrVaultActions>();
                services.AddScoped<AttrKeeperActions>();
                services.AddScoped<AttrVaultEchoAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(Echo));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(Echo, sp.GetRequiredService<AttrVaultEchoAction>()));
            },
            security: SparkTestSecurity.Empty
                .Granting("QueryReadEditNew/AttrVault", "QueryReadEditNew/AttrKeeper", $"{Echo}/AttrVault")
                .Denying("QueryRead/AttrVault/Secret", "QueryRead/AttrKeeper/Code", "Edit/AttrVault/Note", "New/AttrVault/Locked"));
    }

    private readonly SparkEndpointFactory<AttrContext> _factory = host.Factory;
    private readonly SparkClient _client = new(host.Factory.CreateClient(), ownsClient: true);

    public void Dispose() => _client.Dispose();

    // ---- R1: the search oracle --------------------------------------------------------------------

    [Theory]
    [InlineData("beta", "a Query-denied attribute")]
    [InlineData("hiddennote", "a hidden (ShowedOn none) attribute")]
    [InlineData("9911", "a per-row protected attribute that is not on the query surface")]
    public async Task Search_does_not_match_an_attribute_the_caller_may_not_query(string term, string what)
    {
        var result = await _client.ExecuteQueryAsync(VaultsQueryId, search: term);

        result.TotalItems.Should().Be(0, $"searching {what} is a substring oracle on it");
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_still_matches_a_shown_queryable_attribute()
    {
        var result = await _client.ExecuteQueryAsync(VaultsQueryId, search: "bert");

        result.TotalItems.Should().Be(1);
        result.Items.Single().Id.Should().Be("vaults/2");
    }

    // ---- R2: breadcrumbs --------------------------------------------------------------------------

    [Fact]
    public async Task The_breadcrumb_blanks_denied_and_protected_tokens_on_the_row_and_its_reference()
    {
        var po = await _client.GetPersistentObjectAsync(VaultTypeId, "vaults/1");

        po.Should().NotBeNull();
        po!.Breadcrumb.Should().Contain("Anna").And.Contain("Kay");
        po.Breadcrumb.Should().NotContain("xbeta", "Secret is Read-denied");
        po.Breadcrumb.Should().NotContain("9911", "Pin is protected on this row");
        po.Breadcrumb.Should().NotContain("kcode777", "the keeper's Code is Read-denied on its own type");
        // Name is the type, never a copy of the breadcrumb — so it cannot carry the redacted tokens.
        po.Name.Should().Be("AttrVault");

        var keeper = po.Attributes.Single(a => a.Name == "Keeper");
        keeper.Breadcrumb.Should().Be("Kay ", "the reference chip renders the target's breadcrumb with Code blanked — exactly as an empty Code renders");
    }

    [Fact]
    public async Task The_grid_row_breadcrumb_blanks_denied_and_protected_tokens()
    {
        var result = await _client.ExecuteQueryAsync(VaultsQueryId);

        var row = result.Items.Single(i => i.Id == "vaults/1");
        row.Breadcrumb.Should().Contain("Anna").And.Contain("Kay");
        row.Breadcrumb.Should().NotContain("xbeta").And.NotContain("9911").And.NotContain("kcode777");

        var keeperCell = row.Values.Single(v => v.Key == "Keeper");
        keeperCell.Breadcrumb.Should().Be("Kay ");
    }

    // ---- R3: sort, filter, distincts, counts ------------------------------------------------------

    [Fact]
    public async Task Sorting_by_a_denied_column_is_ignored()
    {
        var asc = await _client.ExecuteQueryAsync(VaultsQueryId, sortColumns: [new SortColumn { Property = "Secret", Direction = "asc" }]);
        var desc = await _client.ExecuteQueryAsync(VaultsQueryId, sortColumns: [new SortColumn { Property = "Secret", Direction = "desc" }]);

        desc.Items.Select(i => i.Id).Should().Equal(asc.Items.Select(i => i.Id),
            "a sort that reorders the rows is a comparison oracle on the denied value");
    }

    [Fact]
    public async Task Filtering_on_a_denied_column_is_ignored_and_the_count_is_unaffected()
    {
        var result = await _client.ExecuteQueryAsync(VaultsQueryId,
            columns: [new QueryColumnFilter { Name = "Secret", Includes = ["xbeta"] }]);

        result.TotalItems.Should().Be(3, "an equality filter on the denied value would confirm it through the count");
    }

    [Fact]
    public async Task Distincts_of_a_denied_column_are_empty()
    {
        var result = await _client.GetDistinctValuesAsync(VaultsQueryId, "Secret");

        result.Matching.Should().BeEmpty();
    }

    [Fact]
    public async Task Distincts_of_a_permitted_column_still_list()
    {
        var result = await _client.GetDistinctValuesAsync(VaultsQueryId, "Name");

        result.Matching.Select(v => v.Label).Should().Equal("Anna", "Bert", "Cleo");
    }

    // ---- R4: removal ------------------------------------------------------------------------------

    [Fact]
    public async Task Query_results_omit_a_Query_denied_column_from_columns_and_rows()
    {
        var result = await _client.ExecuteQueryAsync(VaultsQueryId);

        result.Columns.Select(c => c.Name).Should().NotContain("Secret").And.Contain("Name");
        result.Items.Should().OnlyContain(i => i.Values.All(v => v.Key != "Secret"));
    }

    /// <summary>
    /// Also the M1c conflict dialog's half: its re-fetch is this GET, so an attribute removed here can
    /// never reach the dialog's "theirs" column.
    /// </summary>
    [Fact]
    public async Task Get_omits_a_Read_denied_attribute()
    {
        var po = await _client.GetPersistentObjectAsync(VaultTypeId, "vaults/1");

        po!.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
        po.Attributes.Single(a => a.Name == "Note").IsReadOnly.Should().BeTrue("Note is Edit-denied");
    }

    [Fact]
    public async Task Refresh_omits_a_Read_denied_attribute()
    {
        var po = await _client.GetPersistentObjectAsync(VaultTypeId, "vaults/1");

        var refreshed = await _client.RefreshPersistentObjectAsync(po!, "Name");

        refreshed.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
    }

    [Fact]
    public async Task New_omits_a_Read_denied_attribute()
    {
        var po = await _client.NewPersistentObjectAsync(VaultTypeId);

        po.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
    }

    [Fact]
    public async Task A_custom_action_result_omits_a_Read_denied_attribute()
    {
        var result = await _client.ExecuteActionAsync(VaultTypeId, Echo);

        var po = result.GetResult<Po>();
        po.Should().NotBeNull();
        po!.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
    }

    /// <summary>
    /// History's revision view presents through <see cref="IPersistentObjectPresenter"/>. Inside a
    /// request: with no HTTP caller the presenter builds for the system and removes nothing (D13a).
    /// </summary>
    [Fact]
    public async Task The_presenter_omits_a_Read_denied_attribute()
    {
        using var scope = _factory.CreateScope();
        using var _ = AsCaller(scope.ServiceProvider);
        var presenter = scope.ServiceProvider.GetRequiredService<IPersistentObjectPresenter>();
        var revision = new AttrVault { Id = "vaults/1", Name = "Anna", Secret = "xold", Keeper = "keepers/1" };

        var po = await presenter.PresentAsync(VaultTypeId, revision);

        po.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
        po.Breadcrumb.Should().NotContain("xold");
    }

    /// <summary>Makes <paramref name="services"/> the current request's, for code reached outside HTTP.</summary>
    internal static IDisposable AsCaller(IServiceProvider services)
    {
        var accessor = services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
        return new Restore(() => accessor.HttpContext = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [Fact]
    public async Task The_entity_type_definition_is_pruned_per_caller()
    {
        var single = await _client.GetEntityTypeAsync("AttrVault");
        var listed = (await _client.ListEntityTypesAsync()).Single(t => t.Name == "AttrVault");

        foreach (var definition in new[] { single!, listed })
        {
            definition.Attributes.Select(a => a.Name).Should().NotContain("Secret").And.Contain("Name");
            definition.Attributes.Single(a => a.Name == "Note").IsReadOnly.Should().BeTrue("Note is Edit-denied");
            definition.Attributes.Single(a => a.Name == "Name").IsReadOnly.Should().BeFalse();
        }
    }

    /// <summary>
    /// #264 G1/G2: the create form asks <c>?for=new</c>. A New-denied attribute is absent (it was drawn and
    /// refused on save), and an Edit-only deny no longer makes a field read-only on create. Red before the
    /// change: <c>Locked</c> was present and <c>Note</c> read-only. The default shape is unchanged.
    /// </summary>
    [Fact]
    public async Task The_create_form_shape_leaves_out_New_denied_attributes_and_ignores_Edit_denies()
    {
        using var http = _factory.CreateClient();

        var forNew = await AttributesAsync(http, "/spark/types/AttrVault?for=new");
        forNew.Keys.Should().NotContain("Locked", "Locked is New-denied");
        forNew.Keys.Should().NotContain("Secret", "a purpose never widens what is read");
        forNew["Note"].Should().BeFalse("Note is only Edit-denied, and a create is governed by New");

        var byDefault = await AttributesAsync(http, "/spark/types/AttrVault");
        byDefault.Keys.Should().Contain("Locked");
        byDefault["Note"].Should().BeTrue("the default stays the edit shape");
    }

    /// <summary>Attribute name → isReadOnly, as the endpoint sent them.</summary>
    private static async Task<Dictionary<string, bool>> AttributesAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("attributes").EnumerateArray().ToDictionary(
            a => a.GetProperty("name").GetString()!,
            a => a.GetProperty("isReadOnly").GetBoolean());
    }

    [Fact]
    public async Task The_pruned_definition_does_not_leak_into_the_shared_model()
    {
        await _client.GetEntityTypeAsync("AttrVault");

        var model = _factory.GetService<IModelLoader>().GetEntityType(VaultTypeId)!;
        model.Attributes.Select(a => a.Name).Should().Contain("Secret");
        model.Attributes.Single(a => a.Name == "Note").IsReadOnly.Should().BeFalse();
    }

    // ---- fixture ----------------------------------------------------------------------------------

    private static EntityTypeFile VaultModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = VaultTypeId,
            Name = "AttrVault",
            ClrType = typeof(AttrVault).FullName!,
            Breadcrumb = "{Name} {Secret} {Pin} {Keeper}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Secret", DataType = "string", Order = 2 },
                new() { Id = Guid.NewGuid(), Name = "Note", DataType = "string", Order = 3, ShowedOn = 0 },
                new() { Id = Guid.NewGuid(), Name = "Pin", DataType = "string", Order = 4, ShowedOn = EShowedOn.PersistentObject },
                new() { Id = Guid.NewGuid(), Name = "Locked", DataType = "boolean", Order = 5 },
                new() { Id = Guid.NewGuid(), Name = "Keeper", DataType = "Reference", ReferenceType = typeof(AttrKeeper).FullName, Order = 6 },
            ],
        },
        Queries = [new SparkQuery { Id = VaultsQueryId, Name = "AttrVaults", Source = "Database.Vaults", EntityType = "AttrVault" }],
    };

    private static EntityTypeFile KeeperModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = KeeperTypeId,
            Name = "AttrKeeper",
            ClrType = typeof(AttrKeeper).FullName!,
            Breadcrumb = "{Name} {Code}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "Code", DataType = "string" },
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
