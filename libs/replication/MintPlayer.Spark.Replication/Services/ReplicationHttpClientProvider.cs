using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Returns an <see cref="HttpClient"/> pre-configured for cross-module replication
/// calls to a specific target module. Honors per-target cert overrides in
/// <see cref="SparkReplicationCertificateOptions.PerTargetOverrides"/>; falls
/// back to the module's default cert otherwise.
/// </summary>
internal interface IReplicationHttpClientProvider
{
    /// <summary>
    /// Returns an <see cref="HttpClient"/> whose primary handler attaches the
    /// client cert that should be presented when calling
    /// <paramref name="targetModule"/>.
    /// </summary>
    HttpClient GetClient(string targetModule);
}

internal sealed class ReplicationHttpClientProvider : IReplicationHttpClientProvider, IDisposable
{
    private readonly IOptions<SparkReplicationOptions> _optionsAccessor;
    private readonly ConcurrentDictionary<string, HttpClient> _byTarget = new(StringComparer.OrdinalIgnoreCase);

    public ReplicationHttpClientProvider(IOptions<SparkReplicationOptions> optionsAccessor)
    {
        _optionsAccessor = optionsAccessor;
    }

    public HttpClient GetClient(string targetModule)
    {
        // Cache one HttpClient per target. The provider is a singleton so the
        // handlers stay live for the app lifetime. (HttpClientFactory's two-
        // minute rotation isn't a concern here because cross-module replication
        // typically goes through a small, stable set of peers — no DNS-rotation
        // pressure.)
        return _byTarget.GetOrAdd(targetModule, name =>
        {
            var options = _optionsAccessor.Value.ClientCertificate;
            var handler = new HttpClientHandler();

            // Prefer a per-target override, else the module's default cert.
            string? certFile = null;
            string? certPassword = null;
            if (options.PerTargetOverrides.TryGetValue(name, out var perTarget))
            {
                certFile = perTarget.CertificateFile;
                certPassword = perTarget.CertificatePassword;
            }
            else if (!string.IsNullOrEmpty(options.CertificateFile))
            {
                certFile = options.CertificateFile;
                certPassword = options.CertificatePassword;
            }

            if (!string.IsNullOrEmpty(certFile))
            {
                var cert = new X509Certificate2(certFile, certPassword);
                handler.ClientCertificates.Add(cert);
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            }

            return new HttpClient(handler, disposeHandler: true);
        });
    }

    [NoInterfaceMember]
    public void Dispose()
    {
        foreach (var client in _byTarget.Values)
            client.Dispose();
        _byTarget.Clear();
    }
}
