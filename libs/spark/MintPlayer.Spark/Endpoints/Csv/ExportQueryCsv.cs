using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Csv;

namespace MintPlayer.Spark.Endpoints.Csv;

/// <summary>
/// <c>POST /spark/queries/export-csv</c>: a query's rows as a CSV file (warehouse WX11), filtered and sorted as the grid
/// shows them. The read path is the grid's, so the file holds nothing the grid would not show.
/// </summary>
[MemberOf<QueriesGroup>]
internal sealed partial class ExportQueryCsv : IPostEndpoint<SparkCsvExportRequest>
{
    public static string Path => "/export-csv";

    // A read, exempt from antiforgery for the reason /spark/queries/execute is (Execute.cs): a forged one changes
    // nothing and the attacker cannot read the response.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    [Inject] private readonly ISparkCsvTransfer transfer;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "The export request could not be read." }, statusCode: StatusCodes.Status400BadRequest));

    public override async Task<IResult> HandleAsync(SparkCsvExportRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (request.Delimiter is '"' or '\r' or '\n')
            return Results.Json(new { error = "A quote or a line break cannot separate fields." }, statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var file = await transfer.ExportAsync(request, httpContext.RequestAborted);
            if (file is null)
            {
                // Every not-found alike, as /spark/queries/execute answers it (MCP PRD D18).
                var (body, status) = SparkDenial.Refuse(httpContext);
                return status == StatusCodes.Status401Unauthorized
                    ? Results.Json(body, statusCode: status)
                    : Results.Json(new { error = "Query not found" }, statusCode: StatusCodes.Status404NotFound);
            }
            return Results.File(file.Content, file.ContentType, file.FileName);
        }
        catch (SparkValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
