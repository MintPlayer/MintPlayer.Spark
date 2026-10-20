using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Endpoints;

/// <summary><c>/spark/replicas</c>: registration (owner), pushes and digests (consumer), warehouse §4.2.</summary>
internal class SparkReplicasGroup : IEndpointGroup
{
    public static string Prefix => "/spark/replicas";
}

internal class SparkSyncGroup : IEndpointGroup
{
    public static string Prefix => "/spark/sync";
}

/// <summary>
/// The mTLS gate every replication endpoint opens with (R2-C1/C2): the presented client certificate must be the one
/// pinned for the module the caller names. ⚠️ Deliberately no antiforgery metadata on these endpoints: it is enforced
/// before authentication and turned "no module certificate" into a bare 400; an ambient-credentialed caller is checked
/// by the default branch anyway (11.0.0).
/// </summary>
internal static class ModuleCaller
{
    /// <summary>
    /// Validates <paramref name="module"/> and, on success, puts its identity on the request (so the authorization
    /// pipeline judges the module, F17). Returns the refusal to answer, or <see langword="null"/> to go on.
    /// </summary>
    public static async Task<IResult?> AuthenticateAsync(
        IModuleCertificateValidator validator, HttpContext httpContext, string? module, ILogger logger, string what)
    {
        var validation = await validator.ValidateAsync(httpContext, module ?? string.Empty, httpContext.RequestAborted);
        switch (validation)
        {
            case ModuleCertificateValidation.MissingCertificate:
                return Results.Json(new { error = "Client certificate required" }, statusCode: 401);
            case ModuleCertificateValidation.ThumbprintMismatch:
            case ModuleCertificateValidation.UnknownModule:
                logger.LogWarning("{What} refused: certificate validation failed for module '{Module}' ({Reason})", what, module, validation);
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);
        }

        httpContext.EstablishModuleIdentity(module!);
        return null;
    }
}
