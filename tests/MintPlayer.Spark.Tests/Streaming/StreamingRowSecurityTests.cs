using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Streaming;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Streaming;

/// <summary>
/// Row-level security on the streaming path, which had <b>no coverage at all</b>.
/// <para>
/// Streaming is the one row-returning path that reimplements enforcement instead of sharing it, and
/// its only unit tests install <c>PermissiveRowSecurity</c> — under which "allow everything" and
/// "never asked" are indistinguishable. Deleting both enforcement calls from the executor left the
/// entire suite green. These tests close that: one pins that enforcement is <em>invoked</em> per
/// batch, the other that its refusal is <em>honoured</em>.
/// </para>
/// <para>
/// The distinction matters more here than on a paged query. A stream is a long-lived subscription:
/// skipping the check would not merely disclose the rows present when it opened, it would keep
/// disclosing every new one for as long as the client stays connected.
/// </para>
/// </summary>
public class StreamingRowSecurityTests
{
    private static readonly Guid TypeId = Guid.Parse("7d3e11aa-11aa-11aa-11aa-7d3e11aa11aa");

    /// <summary>A document type with a resolvable CLR name, streamed in batches below.</summary>
    public sealed class StreamedDoc
    {
        public string? Id { get; set; }
        public string? Owner { get; set; }
    }

    /// <summary>
    /// Resolved by <c>ActionsResolver</c> in production; supplied directly here. The streaming
    /// method's shape is duck-typed by the executor, so it must match exactly.
    /// </summary>
    public sealed class StreamedDocActions
    {
        public const int BatchCount = 3;
        public const int RowsPerBatch = 2;

        public async IAsyncEnumerable<IReadOnlyList<StreamedDoc>> StreamAll(
            StreamingQueryArgs args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var b = 0; b < BatchCount; b++)
            {
                yield return
                [
                    new StreamedDoc { Id = $"StreamedDocs/{b}-a", Owner = "alice" },
                    new StreamedDoc { Id = $"StreamedDocs/{b}-b", Owner = "bob" },
                ];
                await Task.Yield();
            }
        }
    }

    private readonly IDocumentStore documentStore = Substitute.For<IDocumentStore>();
    private readonly IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
    private readonly IModelLoader modelLoader = Substitute.For<IModelLoader>();
    private readonly IPermissionService permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IBreadcrumbResolver breadcrumbResolver = Substitute.For<IBreadcrumbResolver>();

    private StreamingQueryExecutor CreateExecutor(IRowSecurity rowSecurity)
    {
        var definition = new EntityTypeDefinition
        {
            Id = TypeId,
            Name = "StreamedDoc",
            ClrType = typeof(StreamedDoc).AssemblyQualifiedName,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(StreamedDoc.Id), DataType = "String", ShowedOn = EShowedOn.Query },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(StreamedDoc.Owner), DataType = "String", ShowedOn = EShowedOn.Query },
            ],
        };

        modelLoader.GetEntityTypeByName("StreamedDoc").Returns(definition);
        permissionService.IsAllowedAsync("Query", "StreamedDoc").Returns(true);
        actionsResolver.ResolveForType(typeof(StreamedDoc)).Returns(new StreamedDocActions());
        documentStore.OpenAsyncSession().Returns(_ => Substitute.For<IAsyncDocumentSession>());

        entityMapper
            .ToPersistentObject(Arg.Any<object>(), Arg.Any<Guid>(), Arg.Any<BreadcrumbResult>())
            .Returns(ci =>
            {
                var doc = (StreamedDoc)ci.Arg<object>();
                return new PersistentObject { Id = doc.Id, Name = "StreamedDoc", ObjectTypeId = TypeId };
            });

        return new StreamingQueryExecutor(
            documentStore, entityMapper, modelLoader,
            permissionService, actionsResolver, breadcrumbResolver, rowSecurity);
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "StreamedDocs",
        Source = "Custom.StreamAll",
        EntityType = "StreamedDoc",
    };

    private static async Task<List<StreamingQueryBatch>> Drain(IAsyncEnumerable<StreamingQueryBatch> stream)
    {
        var batches = new List<StreamingQueryBatch>();
        await foreach (var b in stream) batches.Add(b);
        return batches;
    }

    /// <summary>
    /// Both halves of enforcement run on <b>every</b> batch — not once at connect. Filtering without
    /// redacting would still ship protected values on rows the caller may legitimately see, so the
    /// test pins both and pins the arguments, since a call with the wrong action verb or result type
    /// enforces the wrong rule.
    /// </summary>
    [Fact]
    public async Task Every_batch_is_filtered_and_redacted()
    {
        var recorder = new RecordingRowSecurity();
        var executor = CreateExecutor(recorder);

        var batches = await Drain(executor.ExecuteStreamingQueryAsync(Query(), CancellationToken.None));

        batches.Should().HaveCount(StreamedDocActions.BatchCount);

        var filters = recorder.CallsTo(nameof(IRowSecurity.FilterAsync));
        var redactions = recorder.CallsTo(nameof(IRowSecurity.RedactAsync));

        filters.Should().HaveCount(StreamedDocActions.BatchCount,
            "a stream keeps delivering, so enforcement belongs on each batch rather than at connect");
        redactions.Should().HaveCount(StreamedDocActions.BatchCount);

        filters.Should().OnlyContain(c =>
            c.EntityType == typeof(StreamedDoc) &&
            c.ResultType == typeof(StreamedDoc) &&
            c.Action == "Query" &&
            c.RowCount == StreamedDocActions.RowsPerBatch);
    }

    /// <summary>
    /// The refusal is honoured, not merely requested. An executor that called <c>FilterAsync</c> and
    /// then projected from the pre-filter batch it still had in scope would satisfy the test above
    /// and leak every row; only a denying rule catches that.
    /// </summary>
    [Fact]
    public async Task A_denying_rule_puts_zero_rows_on_the_wire()
    {
        var executor = CreateExecutor(new DenyAllRowSecurity());

        var batches = await Drain(executor.ExecuteStreamingQueryAsync(Query(), CancellationToken.None));

        batches.SelectMany(b => b.Items).Should().BeEmpty(
            "a row the rule refuses must not reach the client on any path, and a stream is the path " +
            "where a bypass keeps paying out for as long as the socket is open");
    }

    /// <summary>
    /// Each batch's framework-side work runs on its own session.
    /// <para>
    /// Nothing pinned this before. The connection session is deliberately uncapped for the author's
    /// enumeration, while the framework's own per-batch reads must start from zero each batch — that
    /// is the fix for streams dying around batch 8-15 on a referenced type, and reusing one session
    /// would silently reintroduce it while every row-level assertion still passed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Each_batch_is_enforced_on_a_fresh_session()
    {
        var sessions = new List<IAsyncDocumentSession>();
        var recorder = new RecordingRowSecurity();
        var executor = CreateExecutor(recorder);

        documentStore.OpenAsyncSession().Returns(_ =>
        {
            var session = Substitute.For<IAsyncDocumentSession>();
            sessions.Add(session);
            return session;
        });

        await Drain(executor.ExecuteStreamingQueryAsync(Query(), CancellationToken.None));

        // One connection session, plus one per batch.
        sessions.Should().HaveCount(StreamedDocActions.BatchCount + 1);
        sessions.Distinct().Should().HaveCount(sessions.Count,
            "the per-batch session must be a new one each time, not the connection session reused");
    }
}
