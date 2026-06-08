using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Streaming;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Streaming;

/// <summary>
/// Pure-mock unit tests for <see cref="StreamingQueryExecutor"/> — covers the validation
/// gauntlet (source prefix, EntityType, CLR type, method signature) and the static
/// reflection-cache helpers without standing up a Raven session. Happy-path streaming
/// is covered end-to-end by <c>StreamExecuteQueryTests</c> through the websocket endpoint.
///
/// The class is internal + partial with [Inject] fields; the source generator emits a
/// constructor taking the six services in declaration order.
/// </summary>
public class StreamingQueryExecutorUnitTests
{
    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly IEntityMapper _entityMapper = Substitute.For<IEntityMapper>();
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IBreadcrumbResolver _breadcrumbResolver = Substitute.For<IBreadcrumbResolver>();

    private StreamingQueryExecutor CreateExecutor() => new(
        _documentStore, _entityMapper, _modelLoader,
        _permissionService, _actionsResolver, _breadcrumbResolver);

    private static SparkQuery Q(string source, string? entityType = "TestEntity") => new()
    {
        Id = Guid.NewGuid(),
        Name = "TestStreamingQuery",
        Source = source,
        EntityType = entityType,
    };

    private static async Task<List<PersistentObject[]>> Drain(IAsyncEnumerable<PersistentObject[]> stream)
    {
        var batches = new List<PersistentObject[]>();
        await foreach (var b in stream) batches.Add(b);
        return batches;
    }

    // --- validation gauntlet ---------------------------------------------

    [Fact]
    public async Task Throws_when_source_does_not_start_with_Custom_prefix()
    {
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Database.People"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("Custom.*") && e.Message.Contains("TestStreamingQuery"));
    }

    [Fact]
    public async Task Throws_when_EntityType_is_null_or_empty()
    {
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.Foo", entityType: null), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("EntityType"));
    }

    [Fact]
    public async Task Throws_when_EntityType_is_unknown_to_ModelLoader()
    {
        _modelLoader.GetEntityTypeByName("Ghost").Returns((EntityTypeDefinition?)null);
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.Foo", entityType: "Ghost"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("Ghost") && e.Message.Contains("not found"));
    }

    [Fact]
    public async Task Throws_when_ClrType_is_not_resolvable_from_any_loaded_assembly()
    {
        // EntityTypeDefinition exists, but ClrType points to a class that isn't in any assembly.
        var entityDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "TestEntity",
            ClrType = "NonExistent.Namespace.GhostType",
        };
        _modelLoader.GetEntityTypeByName("TestEntity").Returns(entityDef);
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.Foo"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("CLR type") && e.Message.Contains("GhostType"));
    }

    [Fact]
    public async Task Throws_when_streaming_method_is_not_found_on_actions_class()
    {
        SetupValidEntityAndActions(actions: new ActionsWithoutStreamingMethods());
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.NoSuchMethod"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("NoSuchMethod") && e.Message.Contains("not found"));
    }

    [Fact]
    public async Task Throws_when_method_signature_does_not_match_streaming_contract()
    {
        SetupValidEntityAndActions(actions: new ActionsWithBadSignatures());
        var executor = CreateExecutor();

        // WrongArgs takes (string, CancellationToken) — first arg isn't StreamingQueryArgs.
        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.WrongArgs"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("WrongArgs"));
    }

    [Fact]
    public async Task Throws_when_method_returns_a_non_async_enumerable_type()
    {
        SetupValidEntityAndActions(actions: new ActionsWithBadSignatures());
        var executor = CreateExecutor();

        // ReturnsList returns IReadOnlyList<TestEntity> directly, not IAsyncEnumerable<...>.
        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.ReturnsList"), CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("ReturnsList"));
    }

    [Fact]
    public async Task Permission_check_failure_propagates()
    {
        SetupValidEntityAndActions(actions: new ActionsWithStreamingMethods());
        _permissionService
            .EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedAccessException("nope")));
        var executor = CreateExecutor();

        var act = () => Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.BatchStream"), CancellationToken.None));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // --- happy-path: batch + single-item streams -------------------------

    [Fact]
    public async Task Batch_streaming_method_yields_each_batch_through_the_pipeline()
    {
        SetupValidEntityAndActions(actions: new ActionsWithStreamingMethods());
        StubMapperToEcho();
        var executor = CreateExecutor();

        var batches = await Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.BatchStream"), CancellationToken.None));

        batches.Should().HaveCount(2, "BatchStream yields two batches of two items each");
        batches[0].Should().HaveCount(2);
        batches[1].Should().HaveCount(2);
    }

    [Fact]
    public async Task Single_item_streaming_method_wraps_each_item_in_a_single_element_batch()
    {
        SetupValidEntityAndActions(actions: new ActionsWithStreamingMethods());
        StubMapperToEcho();
        var executor = CreateExecutor();

        var batches = await Drain(executor.ExecuteStreamingQueryAsync(Q("Custom.SingleStream"), CancellationToken.None));

        // SingleStream yields three TestEntity instances — each wrapped in its own array.
        batches.Should().HaveCount(3);
        batches.Should().AllSatisfy(b => b.Should().HaveCount(1));
    }

    // --- helpers ----------------------------------------------------------

    private void SetupValidEntityAndActions(object actions)
    {
        var entityDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "TestEntity",
            // Use the assembly-qualified short name so FindClrType resolves through
            // its t.Name fallback even when FullName disambiguation isn't unique.
            ClrType = nameof(TestEntity),
        };
        _modelLoader.GetEntityTypeByName("TestEntity").Returns(entityDef);
        _actionsResolver.ResolveForType(typeof(TestEntity)).Returns(actions);
        _breadcrumbResolver
            .ResolveAsync(
                Arg.Any<Raven.Client.Documents.Session.IAsyncDocumentSession>(),
                Arg.Any<IReadOnlyList<object>>(),
                Arg.Any<EntityTypeDefinition?>(),
                Arg.Any<CancellationToken>())
            .Returns(BreadcrumbResult.Empty);
        _documentStore.OpenAsyncSession().Returns(Substitute.For<Raven.Client.Documents.Session.IAsyncDocumentSession>());
        _permissionService
            .EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private void StubMapperToEcho()
    {
        _entityMapper
            .ToPersistentObject(Arg.Any<object>(), Arg.Any<Guid>(), Arg.Any<BreadcrumbResult?>())
            .Returns(ci => new PersistentObject
            {
                Id = "echo",
                Name = "TestEntity",
                ObjectTypeId = (Guid)ci.Args()[1]!,
                Attributes = [],
            });
    }

    // --- fixture entity + actions classes --------------------------------

    public class TestEntity
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public class ActionsWithoutStreamingMethods { }

    public class ActionsWithBadSignatures
    {
        // Wrong first arg type — should fail signature validation.
        public IAsyncEnumerable<IReadOnlyList<TestEntity>> WrongArgs(string queryName, CancellationToken ct) => EmptyBatch(ct);

        // Synchronous return — fails ExtractAsyncEnumerableType.
        public IReadOnlyList<TestEntity> ReturnsList(StreamingQueryArgs args, CancellationToken ct)
            => Array.Empty<TestEntity>();

        private static async IAsyncEnumerable<IReadOnlyList<TestEntity>> EmptyBatch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    public class ActionsWithStreamingMethods
    {
        // Batch stream: IAsyncEnumerable<IReadOnlyList<T>> — yields two batches of two.
        public async IAsyncEnumerable<IReadOnlyList<TestEntity>> BatchStream(
            StreamingQueryArgs args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TestEntity[] { new() { Id = "a/1", Name = "A1" }, new() { Id = "a/2", Name = "A2" } };
            await Task.Yield();
            yield return new TestEntity[] { new() { Id = "b/1", Name = "B1" }, new() { Id = "b/2", Name = "B2" } };
        }

        // Single-item stream: IAsyncEnumerable<T> — three items, each wrapped to a 1-batch.
        public async IAsyncEnumerable<TestEntity> SingleStream(
            StreamingQueryArgs args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new() { Id = "s/1", Name = "S1" };
            await Task.Yield();
            yield return new() { Id = "s/2", Name = "S2" };
            yield return new() { Id = "s/3", Name = "S3" };
        }
    }
}
