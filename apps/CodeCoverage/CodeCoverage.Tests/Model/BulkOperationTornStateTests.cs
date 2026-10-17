using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Http;
using Sparrow.Json;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// <see cref="BulkOperation"/> returns the result of an operation whose state was read torn.
/// </summary>
/// <remarks>
/// The server can answer a state read with <c>Completed</c> and no result: it writes
/// <c>Result</c> then <c>Status</c>, and serializes them in the same order without a lock (see
/// <see cref="BulkOperation"/>). The race needs a state read preempted between the two fields,
/// which a loaded full sweep produced once (<c>ApiTokenForgeNeutralRenameTests</c>, 2026-10-17) and
/// an idle machine never does. Here it is made deterministic: the client handle polls, and every
/// <c>Completed</c> it reads has its result stripped, which is exactly the torn answer.
/// </remarks>
public class BulkOperationTornStateTests : CoverageRavenTest
{
    /// <summary>A real GET of the operation state, answered the way a torn server read answers it.</summary>
    private sealed class TornStateCommand(long id, DocumentConventions conventions) : RavenCommand<OperationState>
    {
        public override bool IsReadRequest => false;

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            url = $"{node.Url}/databases/{node.Database}/operations/state?id={id}";
            return new HttpRequestMessage { Method = HttpMethod.Get };
        }

        public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
        {
            if (response is null)
                return;
            Result = conventions.Serialization.DefaultConverter.FromBlittable<OperationState>(response);
            if (Result.Status == OperationStatus.Completed)
                Result.Result = null;
        }
    }

    /// <summary>The client's own <see cref="Operation"/>, polling through <see cref="TornStateCommand"/>.</summary>
    private sealed class TornOperation : Operation
    {
        public TornOperation(RequestExecutor executor, Func<ISingleNodeDatabaseChanges> changes, DocumentConventions conventions, long id, string nodeTag)
            : base(executor, changes, conventions, id, nodeTag)
            => StatusFetchMode = OperationStatusFetchMode.Polling;

        protected override RavenCommand<OperationState> GetOperationStateCommand(DocumentConventions conventions, long id, string? nodeTag = null)
            => new TornStateCommand(id, conventions);
    }

    private static readonly BulkOperation.OperationFactory Torn =
        (executor, changes, conventions, id, nodeTag) => new TornOperation(executor, changes, conventions, id, nodeTag);

    [Fact]
    public async Task A_completed_operation_read_torn_still_yields_its_result()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "ci", Scope = "Account" }, "ApiTokens/1-A");
            await seed.StoreAsync(new ApiToken { Description = "ci", Scope = "Account" }, "ApiTokens/2-A");
            await seed.SaveChangesAsync();
        }

        var result = await BulkOperation.RunAsync(store,
            new PatchByQueryOperation(new IndexQuery { Query = "from ApiTokens as d update { d.Seen = true; }" }),
            Torn, CancellationToken.None);

        result.Should().NotBeNull();
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task The_untorn_path_returns_the_result()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Description = "ci", Scope = "Account" }, "ApiTokens/1-A");
            await seed.SaveChangesAsync();
        }

        var result = await BulkOperation.RunAsync(store,
            new PatchByQueryOperation(new IndexQuery { Query = "from ApiTokens as d update { d.Seen = true; }" }),
            CancellationToken.None);

        result.Total.Should().Be(1);
    }
}
