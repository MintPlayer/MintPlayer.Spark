using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.ReferenceData.Api;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Import;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Search;
using MintPlayer.Spark.ReferenceData.Seed;
using MintPlayer.Spark.ReferenceData.Sources;

namespace MintPlayer.Spark.ReferenceData;

/// <summary>
/// A reference-data instance (<c>docs/reference_data_PRD.md</c>, <c>docs/guide-reference-data.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();   // admin sign-in (RD-DEF 2)
///     spark.AddMessaging();                    // the import queue
///     spark.AddRateLimiter();                  // runs the rate-limiting middleware the /api/v1 policy needs
///     spark.AddReferenceData()                 // model, ledger, imports, releases, /api/v1, the seed
///          .AddBelgium()                       // one line per adapter package (K7)
///          .AddGlobalBaseline();
/// });
/// </code>
/// </summary>
public static class SparkReferenceDataExtensions
{
    /// <summary>The CORS policy of <c>/api/v1</c>: any origin, GET and HEAD, no credentials, ETag and Retry-After exposed.</summary>
    public const string CorsPolicy = "MintPlayer.Spark.ReferenceData.Api";

    /// <summary>The rate-limit policy of <c>/api/v1</c>: a token bucket per IPv4 address or IPv6 /64 (X10).</summary>
    public const string RateLimitPolicy = "MintPlayer.Spark.ReferenceData.Api";

    public static ReferenceDataBuilder AddReferenceData(this ISparkBuilder builder, Action<SparkReferenceDataOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ReferenceDataMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return new ReferenceDataBuilder(builder);
        }

        services.AddSparkConfigurationSection<SparkReferenceDataOptions>();
        if (configure is not null)
            services.Configure(configure);

        var registrationTime = new SparkReferenceDataOptions();
        builder.Configuration?.GetSection(SparkReferenceDataOptions.SectionName).Bind(registrationTime);
        configure?.Invoke(registrationTime);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RefDatasetBuilder>();
        services.TryAddSingleton<RefTypeahead>();
        services.TryAddSingleton<RefDataCurrentRelease>();
        services.TryAddSingleton<RefApiService>();
        services.AddHostedService<RefDataSeeder>();
        if (registrationTime.Releases.Sign)
            services.TryAddSingleton<IReleaseSigner, Ed25519ReleaseSigner>();

        // Downloads (RD1 step 4): one named client, refusing every host no registered source declares.
        services.TryAddTransient<RefDataHostAllowListHandler>();
        services.AddHttpClient(RefDownloads.HttpClientName, client => client.Timeout = registrationTime.Import.DownloadTimeout)
            .AddHttpMessageHandler<RefDataHostAllowListHandler>();

        // The import queue (RD1 step 3): its own lane with a long handler timeout. Never inside a migration.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<RefDataImportMessage>, RefDataImportRecipient>());
        services.Configure<SparkMessagingOptions>(messaging =>
        {
            if (!messaging.Queues.TryGetValue(RefDataImportMessage.Queue, out var queue))
                messaging.Queues[RefDataImportMessage.Queue] = queue = new SparkQueueOptions();
            queue.HandlerTimeout ??= registrationTime.Import.HandlerTimeout;
        });

        // /api/v1 (§4.11): a named CORS policy for the group only (never a default policy, never app.UseCors).
        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .AllowAnyOrigin()
            .WithMethods(HttpMethods.Get, HttpMethods.Head)
            .AllowAnyHeader()
            .WithExposedHeaders("ETag", "Retry-After")));
        services.Configure<RateLimiterOptions>(rl => rl.AddPolicy(RateLimitPolicy, http =>
        {
            var api = http.RequestServices.GetRequiredService<IOptions<SparkReferenceDataOptions>>().Value.Api;
            return RateLimitPartition.GetTokenBucketLimiter(SparkClientAddress.Key(http.Connection.RemoteIpAddress), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = api.TokenLimit,
                TokensPerPeriod = api.TokensPerPeriod,
                ReplenishmentPeriod = api.ReplenishmentPeriod,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }));

        // RX1 for /api/v1: a rejection is readable cross-origin (the limiter runs before the CORS branches, so a browser
        // would otherwise see a network error), carries Retry-After and a JSON body. Other paths keep whatever
        // OnRejected the host or Spark set.
        services.PostConfigure<RateLimiterOptions>(rl =>
        {
            var inner = rl.OnRejected;
            rl.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (http.Request.Path.StartsWithSegments("/api/v1"))
                {
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    http.Response.Headers.AccessControlAllowOrigin = "*";
                    http.Response.Headers.AccessControlExposeHeaders = "Retry-After";
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await http.Response.WriteAsJsonAsync(new Contracts.ApiProblem("too-many-requests", "Slow down, or sync releases instead of crawling the API."), cancellationToken);
                    return;
                }
                if (inner is not null)
                    await inner(context, cancellationToken);
            };
        });

        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkReferenceDataEndpoints());
        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        return new ReferenceDataBuilder(builder);
    }

    /// <summary>Signing asked for without a signer, or a source configured that no adapter registered, refuses startup.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkReferenceDataOptions>>().Value;
        var problems = new List<string>();
        if (options.Releases.Sign && services.GetService<IReleaseSigner>() is null)
            problems.Add("Spark:ReferenceData:Releases:Sign is on, but no IReleaseSigner is registered.");
        if (options.Releases.Sign && services.GetService<IReleaseSigner>() is Ed25519ReleaseSigner)
        {
            try
            {
                ReleaseSignatures.DecodeKey(options.Releases.SigningKey ?? string.Empty, "Spark:ReferenceData:Releases:SigningKey");
            }
            catch (FormatException ex)
            {
                // The message names the setting and the length only, never the value.
                problems.Add(ex.Message + " Set it in user-secrets (--refdata-generate-signing-key <file> writes a new one).");
            }
        }
        var known = services.GetServices<IReferenceDataSource>().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var configured in options.Sources.Keys.Where(k => !known.Contains(k)))
            problems.Add($"Spark:ReferenceData:Sources names '{configured}', which no registered adapter provides.");
        if (problems.Count > 0)
            throw new InvalidOperationException("Reference data cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    private sealed class ReferenceDataMarker;
}

/// <summary>Adapter packages hang their <c>AddBelgium()</c>-style lines off this (K7: one line per adapter).</summary>
public sealed class ReferenceDataBuilder(ISparkBuilder spark)
{
    public ISparkBuilder Spark { get; } = spark;

    /// <summary>
    /// Registers a source and, when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, its scheduled change check
    /// (<c>Schedule</c>, else the adapter's default; 6-field cron, UTC).
    /// </summary>
    public ReferenceDataBuilder AddSource<TSource>() where TSource : class, IReferenceDataSource, new()
    {
        var source = new TSource();
        if (Spark.Services.Any(d => d.ServiceType == typeof(TSource)))
            return this;
        Spark.Services.AddSingleton(source);
        Spark.Services.AddSingleton<IReferenceDataSource>(source);

        var section = Spark.Configuration?.GetSection($"{SparkReferenceDataOptions.SectionName}:Sources:{source.Id}");
        var settings = new ReferenceDataSourceOptions();
        section?.Bind(settings);
        if (settings.Enabled)
        {
            var schedule = string.IsNullOrWhiteSpace(settings.Schedule) ? source.DefaultSchedule : settings.Schedule;
            RefDataSchedules.AddSourceCheck<TSource>(Spark, schedule, "RefData." + source.Id);
        }
        return this;
    }

    public ReferenceDataBuilder AddMapper<TMapper>() where TMapper : class, IReferenceDataMapper
    {
        Spark.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IReferenceDataMapper, TMapper>());
        return this;
    }
}

/// <summary>Signs a release manifest (RD-DEF 5): the detached signature <c>manifest.sig</c> carries.</summary>
public interface IReleaseSigner
{
    byte[] Sign(byte[] manifest);
}

/// <summary>
/// The shipped signer (RS17): Ed25519 with <c>Spark:ReferenceData:Releases:SigningKey</c>. Registered when
/// <c>Releases:Sign</c> is on and the host registered no signer of its own. The key is read when first needed and never
/// logged; startup refuses a missing or malformed key without echoing it.
/// </summary>
internal sealed class Ed25519ReleaseSigner(IOptions<SparkReferenceDataOptions> options) : IReleaseSigner
{
    public byte[] Sign(byte[] manifest)
        => ReleaseSignatures.Sign(ReleaseSignatures.DecodeKey(options.Value.Releases.SigningKey ?? string.Empty, "Spark:ReferenceData:Releases:SigningKey"), manifest);
}
