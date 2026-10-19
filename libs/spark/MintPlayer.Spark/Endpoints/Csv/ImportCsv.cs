using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Csv;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Csv;

/// <summary>
/// <c>POST /spark/po/import-csv</c>: imports a CSV file into one entity type (warehouse WX11), each row through the
/// save path of <c>/spark/po/create</c> and <c>/spark/po/update</c>. Answers <see cref="SparkCsvImportResult"/>: per
/// row created, updated, submitted, skipped or refused with the reasons. <c>dryRun</c> checks and writes nothing.
/// </summary>
[MemberOf<PersistentObjectGroup>]
internal sealed partial class ImportCsv : IPostEndpoint<SparkCsvImportRequest>
{
    public static string Path => "/import-csv";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        // An unknown type and a type the caller may neither create nor edit: the one denial (M-3).
        builder.RefuseDeniedInEnvelope();
    }

    [Inject] private readonly ISparkCsvTransfer transfer;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(ClientResult.EnvelopeRefusal(clientAccessor, context));

    public override async Task<IResult> HandleAsync(SparkCsvImportRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var result = await transfer.ImportAsync(request, httpContext.RequestAborted);
        return ClientResult.Envelope(clientAccessor, result, result.Error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }
}
