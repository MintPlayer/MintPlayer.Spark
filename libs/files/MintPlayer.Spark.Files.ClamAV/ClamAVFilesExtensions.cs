using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MintPlayer.Spark.Files.Scanning;

namespace MintPlayer.Spark.Files.ClamAV;

public static class ClamAVFilesExtensions
{
    /// <summary>
    /// Scans every upload with clamd before it can be claimed:
    /// <code>
    /// spark.AddFiles(files =&gt; files.UseClamAV(o =&gt;
    /// {
    ///     o.Host = "clamav";                     // internal network only
    ///     o.MaxStreamLength = 25 * 1024 * 1024;  // ≤ clamd's StreamMaxLength, ≥ Spark:Files:MaxFileBytes
    /// }));
    /// </code>
    /// Also adds a <c>clamav</c> health check (tagged <c>ready</c>) that scans the EICAR test string and expects a detection.
    /// Ship clamd with this package's <c>clamd.conf</c> (<c>AlertExceedsMax yes</c>): see the README.
    /// </summary>
    public static FilesBuilder UseClamAV(this FilesBuilder files, Action<ClamAVOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ClamAVOptions();
        configure(options);
        var scanner = new ClamAVScanner(options);
        files.Services.AddSingleton(scanner);
        files.Services.AddHealthChecks().AddCheck<ClamAVHealthCheck>("clamav", tags: ["ready"]);
        return files.UseScanner(_ => scanner);
    }

    /// <summary>The same, bound from a configuration section (keys as in <see cref="ClamAVOptions"/>).</summary>
    public static FilesBuilder UseClamAV(this FilesBuilder files, IConfigurationSection section)
        => files.UseClamAV(section.Bind);
}

/// <summary>Healthy only when clamd detects EICAR: a clamd without signatures still answers PING (SP4).</summary>
internal sealed class ClamAVHealthCheck : IHealthCheck
{
    private readonly ClamAVScanner scanner;

    public ClamAVHealthCheck(ClamAVScanner scanner) => this.scanner = scanner;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await scanner.ProbeAsync(cancellationToken);
        return result.Verdict switch
        {
            FileScanVerdict.Infected => HealthCheckResult.Healthy($"clamd detects the EICAR test string ({result.Detail})."),
            FileScanVerdict.Clean => HealthCheckResult.Unhealthy("clamd passed the EICAR test string: its signatures are missing or broken."),
            _ => HealthCheckResult.Unhealthy(result.Detail),
        };
    }
}
