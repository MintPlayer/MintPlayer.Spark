using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Services;

internal interface IModuleCertificateValidator
{
    /// <summary>
    /// Validates that the request's client certificate matches the pinned thumbprint
    /// for <paramref name="requestingModule"/> in SparkModules. Returns a status that
    /// the endpoint maps to 401 / 403 / continue.
    /// </summary>
    Task<ModuleCertificateValidation> ValidateAsync(HttpContext context, string requestingModule, CancellationToken cancellationToken);
}

internal enum ModuleCertificateValidation
{
    Ok,
    /// <summary>Cert required but not presented — map to 401.</summary>
    MissingCertificate,
    /// <summary>Cert presented but doesn't match the pinned thumbprint — map to 403.</summary>
    ThumbprintMismatch,
    /// <summary>RequestingModule is empty or has no entry in SparkModules — map to 403.</summary>
    UnknownModule,
}

[Register(typeof(IModuleCertificateValidator), ServiceLifetime.Singleton)]
internal partial class ModuleCertificateValidator : IModuleCertificateValidator
{
    [Inject] private readonly IOptions<SparkReplicationOptions> optionsAccessor;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<ModuleCertificateValidator> logger;

    /// <summary>
    /// Resolves the configured <see cref="SparkReplicationCertificateMode"/>,
    /// substituting Development for Auto when the host is in Development.
    /// </summary>
    private SparkReplicationCertificateMode ResolveMode()
    {
        var mode = optionsAccessor.Value.ClientCertificate.Mode;
        if (mode == SparkReplicationCertificateMode.Auto)
            return hostEnvironment.IsDevelopment()
                ? SparkReplicationCertificateMode.Development
                : SparkReplicationCertificateMode.Production;
        return mode;
    }

    public async Task<ModuleCertificateValidation> ValidateAsync(HttpContext context, string requestingModule, CancellationToken cancellationToken)
    {
        var mode = ResolveMode();

        if (mode == SparkReplicationCertificateMode.Disabled)
        {
            // Explicit passthrough. It still logs: a mode that turns authentication off on
            // the two most dangerous endpoints in the framework, one JSON string away and
            // leaving no trace, is indistinguishable afterwards from never having been set.
            logger.LogWarning(
                "Cross-module call from '{Module}' accepted with certificate validation Disabled — " +
                "/spark/etl/deploy and /spark/sync/apply are unauthenticated in this process.",
                requestingModule);
            return ModuleCertificateValidation.Ok;
        }

        if (mode == SparkReplicationCertificateMode.Development)
        {
            // Dev mode skips the thumbprint check, but the caller must still name a module
            // that actually registered. This lookup is the part the comment here used to
            // claim and not perform (F1) — without it, Development was a full
            // authentication no-op and `{"RequestingModule": "anything"}` from an
            // unauthenticated caller could write any document in any collection.
            var devModule = await moduleDirectory.FindAsync(requestingModule, cancellationToken);
            if (devModule is null)
                return ModuleCertificateValidation.UnknownModule;

            logger.LogWarning(
                "Cross-module call from '{Module}' accepted in Development mode (no cert thumbprint check). " +
                "Set SparkReplicationOptions.ClientCertificate.Mode = Production before deploying.",
                requestingModule);
            return ModuleCertificateValidation.Ok;
        }

        // Production mode: full thumbprint check.
        if (string.IsNullOrEmpty(requestingModule))
            return ModuleCertificateValidation.UnknownModule;

        var clientCert = context.Connection.ClientCertificate;
        if (clientCert is null)
            return ModuleCertificateValidation.MissingCertificate;

        var moduleInfo = await moduleDirectory.FindAsync(requestingModule, cancellationToken);
        if (moduleInfo is null)
            return ModuleCertificateValidation.UnknownModule;

        // Legacy entries without a pinned thumbprint fail closed. Operators upgrading
        // from pre-mTLS need to either (a) re-register each module so the pin lands,
        // or (b) flip RequireClientCertificate=false in dev while rolling out.
        if (string.IsNullOrEmpty(moduleInfo.ClientCertificateThumbprint))
            return ModuleCertificateValidation.ThumbprintMismatch;

        var presentedThumbprint = clientCert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (!string.Equals(presentedThumbprint, moduleInfo.ClientCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            return ModuleCertificateValidation.ThumbprintMismatch;

        return ModuleCertificateValidation.Ok;
    }
}
