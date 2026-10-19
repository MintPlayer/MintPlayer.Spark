using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Whether the owner's RavenDB licence includes RavenDB ETL (F18, commerce S18 constraint 7).
/// Community has none: <c>AddEtlOperation</c> then throws <c>LicenseLimitException</c>, which the
/// deployment used to answer with a generic 500 that the consumer retried until it dead-lettered.
/// </summary>
internal interface IRavenEtlLicence
{
    /// <summary>
    /// <c>GET /license/status</c> → <c>HasRavenEtl</c>, or <see langword="null"/> when the status
    /// could not be read (RavenDB still refuses on send, and that refusal is mapped the same way).
    /// </summary>
    Task<bool?> HasRavenEtlAsync(CancellationToken cancellationToken);
}

internal sealed partial class RavenEtlLicence : IRavenEtlLicence
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<RavenEtlLicence> logger;

    public async Task<bool?> HasRavenEtlAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await documentStore.Maintenance.Server.SendAsync(new GetRavenEtlLicenceOperation(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "The RavenDB licence status could not be read; the ETL deployment is left to RavenDB to refuse.");
            return null;
        }
    }

    /// <summary><c>GET /license/status</c> → its <c>HasRavenEtl</c> flag.</summary>
    internal sealed class GetRavenEtlLicenceOperation : IServerOperation<bool?>
    {
        public RavenCommand<bool?> GetCommand(DocumentConventions conventions, JsonOperationContext context) => new Command();

        private sealed class Command : RavenCommand<bool?>
        {
            public override bool IsReadRequest => true;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/license/status";
                return new HttpRequestMessage { Method = HttpMethod.Get };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
            {
                if (response is null) return;
                using var json = JsonDocument.Parse(response.ToString());
                Result = json.RootElement.TryGetProperty("HasRavenEtl", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? flag.GetBoolean()
                    : null;
            }
        }
    }
}
