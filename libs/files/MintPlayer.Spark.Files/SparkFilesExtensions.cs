using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Files.Scanning;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Files.Services;
using MintPlayer.Spark.Files.Sniffing;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Files;

/// <summary>
/// File and image attributes (#700, <c>docs/file_attributes_PRD.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddFiles(files => files.ConfigureOptions(o => o.MaxFileBytes = 20 * 1024 * 1024));
/// });
///
/// public class Car
/// {
///     public List&lt;SparkImage&gt; Photos { get; set; } = [];   // Image, isArray
///     public SparkFile? RegistrationDocument { get; set; }   // BinaryFile, needs fileOptions.accept
/// }
/// </code>
/// </summary>
public static class SparkFilesExtensions
{
    /// <summary>
    /// Registers the upload endpoints (<c>/spark/files/prepare</c>, <c>/upload</c>, <c>/commit</c>), the save-path claim
    /// (<see cref="ISparkFileValueHandler"/>), the sniffer and the file store: RavenDB attachments by default, which a store with a schema (SQL Server, PostgreSQL) does not have, so there a file store package is required. Without it a model with a
    /// stored-file attribute refuses to start (D2b).
    /// </summary>
    public static ISparkBuilder AddFiles(this ISparkBuilder builder, Action<FilesBuilder>? configure = null)
    {
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(FilesMarker)))
            throw new InvalidOperationException("AddFiles() is called twice; configure files in one call.");
        services.AddSingleton<FilesMarker>();

        var files = new FilesBuilder(services);
        configure?.Invoke(files);

        services.AddOptions<SparkFilesOptions>();
        services.AddSingleton<IConfigureOptions<SparkFilesOptions>>(sp => new ConfigureNamedOptions<SparkFilesOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(SparkFilesOptions.SectionName).Bind(options)));
        foreach (var action in files.Configure)
            services.Configure(action);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new FileSniffer(files.Signatures));
        services.AddSingleton<UploadRateLimiter>();
        if (files.StoreFactory is { } factory)
            services.AddSingleton(factory);
        else
            if (files.StoreType is { } storeType)
            services.AddSingleton(typeof(IFileStore), storeType);
        else
            services.AddSingleton<IFileStore>(sp => DefaultStore(sp.GetRequiredService<global::MintPlayer.Spark.Abstractions.Storage.ISparkStore>()));
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoredTypeServiceCollectionExtensions.AddSparkStoredType<Staging.SparkFileUpload>(services);
        services.AddSparkFilesServices();
        global::MintPlayer.Spark.Abstractions.Commands.SparkCommandServiceCollectionExtensions.AddSparkCommand<Images.SparkImageVariantCommand>(
            services, Images.SparkImageVariantCommand.Flag,
            "--spark-regenerate-variants [--force]: remakes the stored images' variants that no longer match the configuration.");
        builder.AddInterceptor<FileStoreInterceptor>();
        // A store outside RavenDB is swept for what a crash between commit and store left behind; the service idles otherwise.
        services.AddHostedService<FilesReconcileService>();
        // Deferred scanning only (D6); idles otherwise. Its first sweep waits for the schema (#536 D41).
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoreReadinessServiceCollectionExtensions.AddSparkStoreReadiness(services);
        services.AddHostedService<DeferredFileScanService>();

        // Staging documents expire (IExpiring), and their RavenDB attachments with them.
        builder.Registry.RequireDocumentExpiration("Spark.Files staging");
        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkFilesEndpoints());
        return builder;
    }

    /// <summary>
    /// #536: RavenDB keeps the bytes as attachments of the documents. A store with a schema has no attachments, so it needs
    /// a store outside the database.
    /// </summary>
    internal static IFileStore DefaultStore(global::MintPlayer.Spark.Abstractions.Storage.ISparkStore store)
        => string.Equals(store.ProviderName, global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.RavenDb, StringComparison.OrdinalIgnoreCase)
            ? new RavenAttachmentFileStore(store.AsRavenStore())
            : throw new InvalidOperationException($"Spark.Files keeps files as RavenDB attachments by default, and the {store.ProviderName} provider has none: choose a file store in AddFiles, such as files.UseFileSystem(path), files.UseAzureBlob(...) or files.UseS3(...).");

    /// <summary>The meaning of every <c>fileOptions</c> (core checked its shape): limits within the ceiling, types the sniffer recognises.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkFilesOptions>>().Value;
        var problems = Unmet(options, services.GetRequiredService<FileSniffer>(), services.GetRequiredService<IModelLoader>()).ToList();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Files");
        var hasScanner = services.GetServices<IFileScanner>().Any();
        if (UnscannedProblem(options, hasScanner, services.GetService<IHostEnvironment>()?.IsDevelopment() == true) is { } unscanned)
            problems.Add(unscanned);
        else if (!hasScanner)
            logger?.LogWarning(options.AllowUnscannedFiles
                ? "No IFileScanner is registered and Spark:Files:AllowUnscannedFiles is set: every upload is treated as Clean, unscanned."
                : "No IFileScanner is registered: in Development every upload is treated as Clean, unscanned. Outside Development the app refuses to start without one.");
        if (!services.GetServices<Images.IImageProcessor>().Any()
            && services.GetRequiredService<IModelLoader>().GetEntityTypes().Any(t => t.Attributes.Any(a => a.DataType == SparkFileDataTypes.Image)))
            logger?.LogWarning("Image attributes exist but no IImageProcessor is registered: images are stored as uploaded (metadata such as GPS included) and get no variants. Add MintPlayer.Spark.Files.Images.SkiaSharp and call files.UseSkiaSharpImages().");
        if (problems.Count > 0)
            throw new InvalidOperationException("File attributes are misconfigured:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)));
    }

    /// <summary>
    /// D5: outside Development every upload must be scanned, unless the app says otherwise in so many words. A missing
    /// environment counts as not Development (fail-closed).
    /// </summary>
    internal static string? UnscannedProblem(SparkFilesOptions options, bool hasScanner, bool isDevelopment)
        => hasScanner || isDevelopment || options.AllowUnscannedFiles
            ? null
            : "No IFileScanner is registered. Outside Development every upload must be scanned: register one (files.UseClamAV(…) from MintPlayer.Spark.Files.ClamAV, or files.UseScanner<T>()), or set Spark:Files:AllowUnscannedFiles to accept unscanned files.";

    internal static IReadOnlyList<string> Unmet(SparkFilesOptions options, FileSniffer sniffer, IModelLoader modelLoader)
    {
        var problems = new List<string>();
        if (options.MaxFileBytes <= 0)
            problems.Add("Spark:Files:MaxFileBytes must be positive.");
        if (options.DefaultMaxCount <= 0 || options.MaxBatch <= 0)
            problems.Add("Spark:Files:DefaultMaxCount and MaxBatch must be positive.");
        if (options.StagingLifetime <= TimeSpan.Zero || options.UploadWindow <= TimeSpan.Zero || options.UploadWindow > options.StagingLifetime)
            problems.Add("Spark:Files:UploadWindow must be positive and within StagingLifetime.");
        if (options.ReconcileInterval < TimeSpan.Zero || options.ReconcileGrace <= TimeSpan.Zero)
            problems.Add("Spark:Files:ReconcileInterval must not be negative and ReconcileGrace must be positive.");
        if (options.DownloadUrlLifetime <= TimeSpan.Zero || options.EmbeddedUrlLifetime <= TimeSpan.Zero)
            problems.Add("Spark:Files:DownloadUrlLifetime and EmbeddedUrlLifetime must be positive.");
        if (options.ScanTimeout <= TimeSpan.Zero || options.ScanSweepInterval <= TimeSpan.Zero)
            problems.Add("Spark:Files:ScanTimeout and ScanSweepInterval must be positive.");
        if (options.MaxImagePixels <= 0 || options.MaxConcurrentImageProcessing <= 0 || options.DefaultImageVariants is null
            || options.DefaultImageVariants.Any(v => !Storage.SparkFileKeys.IsVariantName(v.Key) || v.Value is < SparkFileOptions.MinVariantEdge or > SparkFileOptions.MaxVariantEdge))
            problems.Add($"Spark:Files:MaxImagePixels and MaxConcurrentImageProcessing must be positive, and every DefaultImageVariants entry a name of letters, digits, '-' and '_' (at most 64) with an edge between {SparkFileOptions.MinVariantEdge} and {SparkFileOptions.MaxVariantEdge}.");
        if (options.ContentOrigin is { } origin
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.Scheme is not ("https" or "http") || originUri.PathAndQuery != "/"))
            problems.Add("Spark:Files:ContentOrigin must be an absolute http(s) origin without a path.");

        foreach (var type in modelLoader.GetEntityTypes())
        {
            foreach (var attribute in type.Attributes.Where(a => SparkFileDataTypes.IsFile(a.DataType)))
            {
                var where = $"{type.Name}.{attribute.Name}";
                var fileOptions = attribute.FileOptions;
                if (fileOptions?.MaxBytes is { } maxBytes && maxBytes > options.MaxFileBytes)
                    problems.Add($"'{where}': fileOptions.maxBytes {maxBytes} is above Spark:Files:MaxFileBytes ({options.MaxFileBytes}); configuration can only tighten an attribute's limit, never loosen it.");
                foreach (var entry in fileOptions?.Accept ?? [])
                {
                    if (!sniffer.IsRecognised(entry))
                        problems.Add($"'{where}': fileOptions.accept '{entry}' is not recognisable from a file's content. Register its magic bytes with files.AddSignature(…).");
                }
            }
        }
        return problems;
    }

    private sealed class FilesMarker;
}
