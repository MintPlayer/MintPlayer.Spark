using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;

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
    [Inject] private readonly ModuleRegistrationService registrationService;
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
            // Explicit passthrough — no validation, no warning. Caller chose this.
            return ModuleCertificateValidation.Ok;
        }

        if (mode == SparkReplicationCertificateMode.Development)
        {
            // Dev mode: skip cert thumbprint check, still log every call so the
            // relaxed posture is visible in logs. Requesting module must still
            // exist in SparkModules — that's free identity-level sanity.
            if (string.IsNullOrEmpty(requestingModule))
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

        // RavenDB lookup of the pinned thumbprint. We use the registration service's
        // dedicated SparkModules store rather than the app's request-scoped session
        // because this collection lives in a different database.
        using var modulesStore = registrationService.CreateModulesStore();
        using var session = modulesStore.OpenAsyncSession();
        var moduleInfo = await session.LoadAsync<ModuleInformation>(
            $"moduleInformations/{requestingModule}", cancellationToken);

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
