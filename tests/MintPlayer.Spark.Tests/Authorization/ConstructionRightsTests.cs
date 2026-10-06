using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;
using Raven.Client.Documents.Linq;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// Top-level, not nested: the right's type name is the CLR name's last dotted segment.
public class CanaryVault
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string ZqxCanaryAttr { get; set; } = "";
    public string Note { get; set; } = "";
    public string? ZqxBlindAttr { get; set; }
}

/// <summary>A sub-query row whose reference to its <see cref="CanaryVault"/> the caller may not query.</summary>
public class CanaryVaultEntry
{
    public string? Id { get; set; }
    public string Text { get; set; } = "";
    public string? ZqxParentRefAttr { get; set; }
}

/// <summary>
/// D13a, milestone M8: persistent objects are built for the caller, and nothing a caller may not read
/// reaches any response — neither its value nor that it exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>ZqxCanaryAttr</c> is the canary twice over: its <em>name</em> appears nowhere but in the model,
/// and its stored <em>value</em> nowhere but in the document. The caller holds every right on the type
/// and none on that attribute (Query, Read, Edit and New denied), and <c>Note</c> is Edit-denied only.
/// The model makes the name as tempting to leak as it can: the type's breadcrumb template names it, a
/// renderer option names it, the query sorts by it and overrides its column, and it is required, so a
/// save that lost its stored value would fail validation.
/// </para>
/// <para>
/// Two more canaries close M8's known gaps (composition M9). <c>ZqxBlindAttr</c> is required and
/// write-only (Query and Read denied, Edit and New granted), so a create that does not post it must
/// not fail "required" by its name. <c>ZqxParentRefAttr</c> is the Query-denied <c>parentReference</c>
/// of a real sub-query, which type and query metadata must leave out.
/// </para>
/// <para>
/// Names start with <c>Canary</c> because <c>ActionsResolver</c> matches actions classes by simple name
/// across the whole assembly.
/// </para>
/// </remarks>
public class ConstructionRightsTests(ConstructionRightsTests.Host host)
    : SparkSharedTestDriver(host), IClassFixture<ConstructionRightsTests.Host>, IDisposable
{
    private static readonly Guid VaultTypeId = Guid.Parse("c4a40000-0000-4000-8000-00000000c001");
    private static readonly Guid VaultsQueryId = Guid.Parse("c4a40000-0000-4000-8000-00000000c002");
    private static readonly Guid EntryTypeId = Guid.Parse("c4a40000-0000-4000-8000-00000000c003");
    private static readonly Guid EntriesQueryId = Guid.Parse("c4a40000-0000-4000-8000-00000000c004");

    private const string CanaryName = nameof(CanaryVault.ZqxCanaryAttr);
    private const string CanaryValue = "zqx-canary-value-7f3e";
    private const string BlindName = nameof(CanaryVault.ZqxBlindAttr);
    private const string ParentRefName = nameof(CanaryVaultEntry.ZqxParentRefAttr);
    private const string Echo = "CanaryEcho";
    private const string Prompt = "CanaryPrompt";
    private const string Leak = "CanaryLeak";
    private const string Refresh = "CanaryRefresh";

    public sealed class CanaryContext : SparkContext
    {
        public IRavenQueryable<CanaryVault> CanaryVaults => Session.Query<CanaryVault>();
        public IRavenQueryable<CanaryVaultEntry> CanaryVaultEntries => Session.Query<CanaryVaultEntry>();
    }

    public class CanaryVaultActions(IEntityMapper mapper)
        : DefaultPersistentObjectActions<CanaryVault>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
    {
        public string RowSecurityRationale => "Test fixture: every row is visible; the subject is attribute rights.";
    }

    /// <summary>A loaded object in an action's result.</summary>
    public sealed class CanaryEchoAction(IDatabaseAccess databaseAccess) : ICustomAction
    {
        public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
            => args.SetResult(await databaseAccess.GetPersistentObjectAsync(VaultTypeId, "canaryvaults/1"));
    }

    /// <summary>A retry prompt scaffolded through IManager, whose hook also writes the hidden attribute.</summary>
    public sealed class CanaryPromptAction(IManager manager) : ICustomAction
    {
        public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            var prompt = await manager.GetPersistentObjectAsync(VaultTypeId, cancellationToken: cancellationToken);
            prompt[CanaryName].Value = CanaryValue; // pruned for this caller: a silent no-op
            manager.Retry.Action(title: "Confirm", options: ["OK"], persistentObject: prompt, cancellable: true);
        }
    }

    /// <summary>
    /// The mistake the boundary net exists for: a system construction handed to the client. The host
    /// runs outside Development, so the net prunes it rather than throwing.
    /// </summary>
    public sealed class CanaryLeakAction(IEntityMapper mapper, IDatabaseAccess databaseAccess) : ICustomAction
    {
        public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            var vault = await databaseAccess.GetDocumentUncheckedAsync<CanaryVault>("canaryvaults/1");
            args.SetResult(mapper.AsSystem().ToPersistentObject(vault!, VaultTypeId));
        }
    }

    /// <summary>A refresh that names a hidden attribute without an object (M8's first known gap).</summary>
    public sealed class CanaryRefreshAction(IManager manager) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            manager.Client.RefreshAttribute(VaultTypeId, "canaryvaults/1", CanaryName, CanaryValue);
            manager.Client.RefreshAttribute(VaultTypeId, "canaryvaults/1", BlindName, CanaryValue);
            manager.Client.RefreshAttribute(VaultTypeId, "canaryvaults/1", "Name", "Anna");
            return Task.CompletedTask;
        }
    }

    public sealed class Host : SharedSparkHost<CanaryContext>
    {
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();

            using var session = Store.OpenAsyncSession();
            session.Advanced.WaitForIndexesAfterSaveChanges(RavenIndexingExtensions.DefaultTimeout, throwOnTimeout: true);
            await session.StoreAsync(new CanaryVault { Name = "Anna", ZqxCanaryAttr = CanaryValue, Note = "first" }, "canaryvaults/1");
            await session.StoreAsync(new CanaryVault { Name = "Bert", ZqxCanaryAttr = CanaryValue + "-b", Note = "second" }, "canaryvaults/2");
            await session.SaveChangesAsync();
        }

        protected override SparkEndpointFactory<CanaryContext> CreateFactory() => new(
            Store,
            [VaultModel(), EntryModel()],
            configureServices: services =>
            {
                services.AddScoped<CanaryVaultActions>();
                services.AddScoped<CanaryEchoAction>();
                services.AddScoped<CanaryPromptAction>();
                services.AddScoped<CanaryLeakAction>();
                services.AddScoped<CanaryRefreshAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(Echo, Prompt, Leak, Refresh));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(new Dictionary<string, ICustomAction>(StringComparer.OrdinalIgnoreCase)
                {
                    [Echo] = sp.GetRequiredService<CanaryEchoAction>(),
                    [Prompt] = sp.GetRequiredService<CanaryPromptAction>(),
                    [Leak] = sp.GetRequiredService<CanaryLeakAction>(),
                    [Refresh] = sp.GetRequiredService<CanaryRefreshAction>(),
                }));
            },
            security: SparkTestSecurity.Empty
                .Granting("QueryReadEditNew/CanaryVault", $"{Echo}/CanaryVault", $"{Prompt}/CanaryVault", $"{Leak}/CanaryVault",
                    $"{Refresh}/CanaryVault", "QueryReadEditNew/CanaryVaultEntry")
                .Denying($"QueryReadEditNew/CanaryVault/{CanaryName}", "Edit/CanaryVault/Note", $"QueryRead/CanaryVault/{BlindName}",
                    $"QueryRead/CanaryVaultEntry/{ParentRefName}"));
    }

    private readonly SparkEndpointFactory<CanaryContext> _factory = host.Factory;
    private readonly List<string> _bodies = [];

    private SparkClient Client => _recording ??= CreateRecordingClient();
    private SparkClient? _recording;

    public void Dispose() => _recording?.Dispose();

    // ---- the canary leak test (D13a constraint 2) -----------------------------------------------------

    /// <summary>
    /// Every response the client can provoke on this type, recorded raw off the wire: neither the
    /// canary's value nor its name may appear in any of them.
    /// </summary>
    [Fact]
    public async Task No_response_carries_a_hidden_attributes_value_or_name()
    {
        // Persistent object: get, new, refresh, save.
        var po = await Client.GetPersistentObjectAsync(VaultTypeId, "canaryvaults/1");
        await Client.NewPersistentObjectAsync(VaultTypeId);
        await Client.RefreshPersistentObjectAsync(po!, "Name");
        po!["Name"].SetValue("Anna");
        await Client.UpdatePersistentObjectAsync(po);

        // A create that leaves the required, write-only attribute out: it succeeds, and no
        // validation error names it.
        var scaffold = await Client.NewPersistentObjectAsync(VaultTypeId);
        scaffold!["Name"].SetValue("Cleo");
        await Client.CreatePersistentObjectAsync(scaffold);

        // Query rows (and their breadcrumbs), type metadata and query metadata — the sub-query's
        // parentReference among them, on the parent type and on the query.
        await Client.ExecuteQueryAsync(VaultsQueryId);
        await Client.GetEntityTypeAsync("CanaryVault");
        await Client.GetEntityTypeAsync("CanaryVaultEntry");
        await Client.ListEntityTypesAsync();
        await Client.GetQueryAsync(VaultsQueryId);
        await Client.GetQueryAsync(EntriesQueryId);
        await Client.ListQueriesAsync();

        // An action's result, a retry prompt, the net's fallback for a system object, and a refresh
        // that names attributes without an object.
        await Client.ExecuteActionAsync(VaultTypeId, Echo);
        var prompted = await Client.ExecuteActionAsync(VaultTypeId, Prompt);
        prompted.IsRetry.Should().BeTrue("the prompt is part of what is under test");
        await Client.ExecuteActionAsync(VaultTypeId, Leak);
        await Client.ExecuteActionAsync(VaultTypeId, Refresh);

        // An error: a row that does not exist.
        await Client.GetPersistentObjectAsync(VaultTypeId, "canaryvaults/404");

        _bodies.Should().HaveCountGreaterThanOrEqualTo(19);
        foreach (var body in _bodies)
        {
            body.Should().NotContain(CanaryValue, "a hidden attribute's value must never reach a response");
            body.Should().NotContainEquivalentOf(CanaryName, "not even that the hidden attribute exists");
            body.Should().NotContainEquivalentOf(BlindName, "nor a write-only one the caller did not post");
            body.Should().NotContainEquivalentOf(ParentRefName, "nor a parentReference the caller may not query");
        }
    }

    /// <summary>
    /// M8's first known gap: the object-less refresh is judged like the object one. A hidden attribute
    /// is a silent no-op, a visible one is refreshed, and a name the type never had throws.
    /// </summary>
    [Fact]
    public void RefreshAttribute_by_type_and_id_skips_an_attribute_the_caller_may_not_read()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var client = scope.ServiceProvider.GetRequiredService<IManager>().Client;

        client.RefreshAttribute(VaultTypeId, "canaryvaults/1", CanaryName, CanaryValue);
        client.RefreshAttribute(VaultTypeId, "canaryvaults/1", BlindName, CanaryValue);
        client.RefreshAttribute(VaultTypeId, "canaryvaults/1", "Name", "Anna");

        client.Operations.OfType<Abstractions.ClientOperations.RefreshAttributeOperation>()
            .Select(o => o.AttributeName).Should().Equal("Name");
        var unknown = () => client.RefreshAttribute(VaultTypeId, "canaryvaults/1", "NoSuchAttribute", null);
        unknown.Should().Throw<InvalidOperationException>("only a hidden name is forgiven");
    }

    /// <summary>
    /// M8's third known gap. The required attribute is write-only for this caller, so the create form
    /// leaves it out; a create that does not post it keeps the CLR default and is not validated on it,
    /// because a "required" error would name it. Posting it is a deliberate write, and lands.
    /// </summary>
    [Fact]
    public async Task A_create_does_not_validate_a_required_attribute_the_caller_cannot_see()
    {
        var scaffold = await Client.NewPersistentObjectAsync(VaultTypeId);
        scaffold!.Attributes.Select(a => a.Name).Should().NotContain(BlindName);
        scaffold["Name"].SetValue("Dirk");

        var created = await Client.CreatePersistentObjectAsync(scaffold);

        created.Attributes.Select(a => a.Name).Should().NotContain(BlindName);
        (await LoadAsync<CanaryVault>(created.Id!))!.ZqxBlindAttr.Should().BeNull();

        var blind = await Client.NewPersistentObjectAsync(VaultTypeId);
        blind!["Name"].SetValue("Emma");
        blind.AddAttribute(new PersistentObjectAttribute { Name = BlindName, Value = "written", IsValueChanged = true });
        var written = await Client.CreatePersistentObjectAsync(blind);
        (await LoadAsync<CanaryVault>(written.Id!))!.ZqxBlindAttr.Should().Be("written", "a write-only attribute still takes a posted value");
    }

    [Fact]
    public async Task Type_and_query_metadata_drop_a_parentReference_the_caller_may_not_query()
    {
        var parent = await Client.GetEntityTypeAsync("CanaryVault");
        var query = await Client.GetQueryAsync(EntriesQueryId);

        parent!.Queries.Should().ContainSingle().Which.ParentReference.Should().BeNull();
        query!.ParentReference.Should().BeNull();
    }

    /// <summary>
    /// A caller who posts the hidden attribute anyway learns nothing about the stored value from the
    /// answer. Only the value is checked: the caller supplied the name.
    /// </summary>
    [Fact]
    public async Task Posting_a_hidden_attribute_does_not_echo_its_stored_value()
    {
        var po = await Client.GetPersistentObjectAsync(VaultTypeId, "canaryvaults/2");
        po!.AddAttribute(new PersistentObjectAttribute { Name = CanaryName, Value = "posted", IsValueChanged = true });

        try
        {
            await Client.UpdatePersistentObjectAsync(po);
        }
        catch (SparkClientException)
        {
            // A refusal is an acceptable answer; its message is what is checked below.
        }

        _bodies.Should().NotBeEmpty();
        _bodies.Should().AllSatisfy(body => body.Should().NotContain(CanaryValue));
    }

    [Fact]
    public async Task The_breadcrumb_template_drops_the_hidden_token_and_keeps_the_rest()
    {
        var type = await Client.GetEntityTypeAsync("CanaryVault");

        type!.Breadcrumb.Should().Be("{Name} ");
        type.Attributes.Single(a => a.Name == "Name").RendererOptions.Should().BeNull("its only option named the hidden attribute");
    }

    [Fact]
    public async Task Query_metadata_drops_sort_and_column_overrides_on_the_hidden_attribute()
    {
        var query = await Client.GetQueryAsync(VaultsQueryId);

        query!.SortColumns.Should().BeEmpty();
        query.Columns.Should().BeEmpty();
    }

    // ---- construction (PRD §7 acceptance) -----------------------------------------------------------

    [Fact]
    public async Task GetPersistentObject_builds_an_object_pruned_for_a_caller_lacking_an_attribute_right()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var manager = scope.ServiceProvider.GetRequiredService<IManager>();

        var po = await manager.GetPersistentObjectAsync(VaultTypeId);

        po.Attributes.Select(a => a.Name).Should().NotContain(CanaryName).And.Contain("Name");
        po.Attributes.Single(a => a.Name == "Note").IsReadOnly.Should().BeTrue("Note is Edit-denied");
        po.TryGetAttribute(CanaryName, out var hidden).Should().BeFalse();
        hidden.Should().BeNull();
    }

    [Fact]
    public async Task A_create_shape_is_governed_by_New_not_Edit()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var mapper = scope.ServiceProvider.GetRequiredService<IEntityMapper>();

        var po = await mapper.GetPersistentObjectAsync(VaultTypeId, "New");

        po.Attributes.Select(a => a.Name).Should().NotContain(CanaryName);
        po.Attributes.Single(a => a.Name == "Note").IsReadOnly.Should().BeFalse("Note is only Edit-denied");
    }

    [Fact]
    public async Task ToPersistentObject_prunes_a_mapped_entity_for_the_caller()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var mapper = scope.ServiceProvider.GetRequiredService<IEntityMapper>();

        var po = await mapper.ToPersistentObjectAsync(new CanaryVault { Id = "canaryvaults/9", Name = "N", ZqxCanaryAttr = CanaryValue });

        po.Attributes.Select(a => a.Name).Should().NotContain(CanaryName);
        po.Attributes.Should().OnlyContain(a => !Equals(a.Value, CanaryValue));
    }

    /// <summary>ForgeAccountsActions writes a Read-denied attribute for every caller; it must keep working.</summary>
    [Fact]
    public async Task Writing_to_a_pruned_attribute_is_a_silent_no_op()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var po = await scope.ServiceProvider.GetRequiredService<IManager>().GetPersistentObjectAsync(VaultTypeId);

        po[CanaryName].Value = CanaryValue;
        po[CanaryName].SetValue(CanaryValue);

        po.Attributes.Select(a => a.Name).Should().NotContain(CanaryName);
        po[CanaryName].Value.Should().BeNull("a pruned attribute reads as empty");
        var unknown = () => po["NoSuchAttribute"];
        unknown.Should().Throw<KeyNotFoundException>("only a pruned name is forgiven");
    }

    [Fact]
    public async Task Outside_a_request_the_caller_is_the_system_and_nothing_is_removed()
    {
        using var scope = _factory.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<IEntityMapper>();

        var po = await mapper.GetPersistentObjectAsync(VaultTypeId);

        po.Attributes.Select(a => a.Name).Should().Contain(CanaryName);
    }

    // ---- AsSystem keeps what system work needs (S5) ---------------------------------------------------

    [Fact]
    public void AsSystem_builds_the_whole_object_inside_a_request()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var mapper = scope.ServiceProvider.GetRequiredService<IEntityMapper>();

        var po = mapper.AsSystem().ToPersistentObject(new CanaryVault { Id = "canaryvaults/9", ZqxCanaryAttr = CanaryValue }, VaultTypeId);

        po[CanaryName].Value.Should().Be(CanaryValue);
        scope.ServiceProvider.GetRequiredService<IManager>().AsSystem().GetPersistentObject(VaultTypeId)
            .Attributes.Select(a => a.Name).Should().Contain(CanaryName);
    }

    /// <summary>Replication writes every synced field, whoever the request is.</summary>
    [Fact]
    public void Sync_builds_the_whole_object_inside_a_request()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var handler = (SyncActionHandler)scope.ServiceProvider.GetRequiredService<ISyncActionHandler>();

        var po = handler.BuildPersistentObject(typeof(CanaryVault), "canaryvaults/9",
            new Dictionary<string, object?> { ["Name"] = "N", [CanaryName] = CanaryValue }, properties: null);

        po[CanaryName].Value?.ToString().Should().Be(CanaryValue);
    }

    /// <summary>
    /// The hidden attribute is required and the caller may not write it, so validation reads its stored
    /// value. Pruned, the stored value would be missing and the save would fail "required".
    /// </summary>
    [Fact]
    public async Task Save_validation_reads_the_stored_value_of_an_attribute_the_caller_may_not_see()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);
        var validation = scope.ServiceProvider.GetRequiredService<ISaveValidation>();
        var definition = scope.ServiceProvider.GetRequiredService<IModelLoader>().GetEntityType(VaultTypeId)!;
        var shielded = new Po
        {
            Id = "canaryvaults/1",
            Name = "CanaryVault",
            ObjectTypeId = VaultTypeId,
            Attributes = [new PersistentObjectAttribute { Name = "Name", Value = "Anna", IsValueChanged = true }],
        };
        var stored = new CanaryVault { Id = "canaryvaults/1", Name = "Anna", ZqxCanaryAttr = CanaryValue };

        validation.Request(shielded);
        var act = () => validation.ValidateAsync(shielded, definition, stored, new HashSet<string>([CanaryName]));

        await act.Should().NotThrowAsync();
    }

    // ---- the boundary net ---------------------------------------------------------------------------

    [Fact]
    public void In_Development_an_unpresented_object_in_a_response_throws_and_names_its_type()
    {
        using var scope = _factory.CreateScope();
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns(Environments.Development);
        using var _ = AttributeRightsEnforcementTests.AsCaller(new Overriding(scope.ServiceProvider, typeof(IHostEnvironment), development));

        var act = () => JsonSerializer.Serialize(Unpresented(), NetOptions());

        act.Should().Throw<InvalidOperationException>().WithMessage("*CanaryVault*");
    }

    [Fact]
    public void Outside_Development_an_unpresented_object_is_pruned_for_the_caller()
    {
        using var scope = _factory.CreateScope();
        using var _ = AttributeRightsEnforcementTests.AsCaller(scope.ServiceProvider);

        var json = JsonSerializer.Serialize(Unpresented(), NetOptions());

        json.Should().NotContain(CanaryValue).And.NotContain(CanaryName).And.Contain("Anna");
    }

    [Fact]
    public async Task A_presented_object_passes_the_net_untouched()
    {
        using var scope = _factory.CreateScope();
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns(Environments.Development);
        using var _ = AttributeRightsEnforcementTests.AsCaller(new Overriding(scope.ServiceProvider, typeof(IHostEnvironment), development));
        var po = await scope.ServiceProvider.GetRequiredService<IEntityMapper>()
            .ToPersistentObjectAsync(new CanaryVault { Id = "canaryvaults/9", Name = "Anna" });

        var json = JsonSerializer.Serialize(po, NetOptions());

        json.Should().Contain("Anna");
    }

    [Fact]
    public async Task The_net_fallback_prunes_a_system_object_an_action_returns()
    {
        var result = await Client.ExecuteActionAsync(VaultTypeId, Leak);

        var po = result.GetResult<Po>();
        po.Should().NotBeNull();
        po!.Attributes.Select(a => a.Name).Should().NotContain(CanaryName).And.Contain("Name");
    }

    // ---- fixture ----------------------------------------------------------------------------------

    private static Po Unpresented() => new()
    {
        Id = "canaryvaults/1",
        Name = "CanaryVault",
        ObjectTypeId = VaultTypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Name", Value = "Anna" },
            new PersistentObjectAttribute { Name = CanaryName, Value = CanaryValue },
        ],
    };

    private static JsonSerializerOptions NetOptions()
    {
        var options = new JsonSerializerOptions();
        SparkPresentation.Install(options);
        return options;
    }

    /// <summary>A client whose every response body is kept, raw, before the client reads it.</summary>
    private SparkClient CreateRecordingClient()
    {
        var inner = _factory.CreateClient();
        var outer = new HttpClient(new Recording(inner, _bodies)) { BaseAddress = inner.BaseAddress };
        return new SparkClient(outer, ownsClient: true);
    }

    private sealed class Recording(HttpClient inner, List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await inner.SendAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (bodies)
                bodies.Add(System.Text.Encoding.UTF8.GetString(bytes));

            var replay = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers)
                replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = replay;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Overriding(IServiceProvider inner, Type type, object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == type ? instance : inner.GetService(serviceType);
    }

    private static EntityTypeFile VaultModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = VaultTypeId,
            Name = "CanaryVault",
            ClrType = typeof(CanaryVault).FullName!,
            Breadcrumb = "{Name} {" + CanaryName + "}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1, RendererOptions = new() { ["titleAttribute"] = CanaryName } },
                new() { Id = Guid.NewGuid(), Name = CanaryName, DataType = "string", Order = 2, IsRequired = true },
                new() { Id = Guid.NewGuid(), Name = "Note", DataType = "string", Order = 3 },
                new() { Id = Guid.NewGuid(), Name = BlindName, DataType = "string", Order = 4, IsRequired = true },
            ],
            Queries = [new SparkSubQuery { Query = "CanaryVaultEntries", ParentReference = ParentRefName }],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = VaultsQueryId,
                Name = "CanaryVaults",
                Source = "Database.CanaryVaults",
                EntityType = "CanaryVault",
                SortColumns = [new SortColumn { Property = CanaryName }],
                Columns = [new SparkQueryColumn { Name = CanaryName, CanSort = true }],
            },
        ],
    };

    /// <summary>A real reference, so startup's parentReference validation accepts the sub-query.</summary>
    private static EntityTypeFile EntryModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = EntryTypeId,
            Name = "CanaryVaultEntry",
            ClrType = typeof(CanaryVaultEntry).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = ParentRefName, DataType = "Reference", ReferenceType = typeof(CanaryVault).FullName, Order = 2 },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = EntriesQueryId,
                Name = "CanaryVaultEntries",
                Source = "Database.CanaryVaultEntries",
                EntityType = "CanaryVaultEntry",
                ParentReference = ParentRefName,
            },
        ],
    };

    private async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private sealed class StubActionResolver(IReadOnlyDictionary<string, ICustomAction> actions) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string requested) => actions.GetValueOrDefault(requested);

        public IReadOnlyList<string> GetRegisteredActionNames() => [.. actions.Keys];
    }
}
