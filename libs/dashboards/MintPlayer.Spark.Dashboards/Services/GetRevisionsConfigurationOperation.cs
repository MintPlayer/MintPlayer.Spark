using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Http;
using Sparrow.Json;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// <c>GET /databases/{db}/revisions/config</c>, the database-level read of the revisions configuration: RavenDB
/// 7.2.6's client has no operation for it and <c>GetDatabaseRecordOperation</c> is server-wide. 404 (no
/// configuration yet) reads as <see langword="null"/>. The same request as History's <c>RevisionsConfigurator</c>
/// (spike H4), which this package does not reference.
/// </summary>
internal sealed class GetRevisionsConfigurationOperation : IMaintenanceOperation<RevisionsConfiguration?>
{
    public RavenCommand<RevisionsConfiguration?> GetCommand(DocumentConventions conventions, JsonOperationContext context)
        => new Command(conventions);

    private sealed class Command(DocumentConventions conventions) : RavenCommand<RevisionsConfiguration?>
    {
        public override bool IsReadRequest => false;

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            url = $"{node.Url}/databases/{node.Database}/revisions/config";
            return new HttpRequestMessage { Method = HttpMethod.Get };
        }

        public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
            => Result = response is null ? null : conventions.Serialization.DefaultConverter.FromBlittable<RevisionsConfiguration>(response);
    }
}
