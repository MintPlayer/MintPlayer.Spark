using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Files.Sniffing;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files;

/// <summary>What <c>spark.AddFiles(files =&gt; …)</c> configures.</summary>
public sealed class FilesBuilder
{
    internal FilesBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    internal List<Action<SparkFilesOptions>> Configure { get; } = [];

    internal List<ISparkFileSignature> Signatures { get; } = [];

    internal Type? StoreType { get; private set; }

    internal Func<IServiceProvider, IFileStore>? StoreFactory { get; private set; }

    /// <summary>Changes <see cref="SparkFilesOptions"/> (applied after <c>Spark:Files</c>, so code wins).</summary>
    public FilesBuilder ConfigureOptions(Action<SparkFilesOptions> configure)
    {
        Configure.Add(configure);
        return this;
    }

    /// <summary>Recognises another format by its magic bytes, so an attribute can accept it (D4).</summary>
    public FilesBuilder AddSignature(ISparkFileSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        Signatures.Add(signature);
        return this;
    }

    /// <summary>Adds an upload hook; hooks run in the order added.</summary>
    public FilesBuilder AddUploadHandler<THandler>() where THandler : class, IFileUploadHandler
    {
        Services.AddScoped<IFileUploadHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Scans every upload with <typeparamref name="TScanner"/> (a singleton). Without a scanner, Production refuses to
    /// start unless <see cref="SparkFilesOptions.AllowUnscannedFiles"/> is set (D5).
    /// </summary>
    public FilesBuilder UseScanner<TScanner>() where TScanner : class, Scanning.IFileScanner
    {
        Services.AddSingleton<Scanning.IFileScanner, TScanner>();
        return this;
    }

    /// <inheritdoc cref="UseScanner{TScanner}()"/>
    public FilesBuilder UseScanner(Func<IServiceProvider, Scanning.IFileScanner> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Decodes and re-encodes every <c>Image</c> upload with <typeparamref name="TProcessor"/> (a singleton), and makes its
    /// variants. The image packages call this from <c>UseSkiaSharpImages()</c>/<c>UseNetVipsImages()</c>. The last one wins.
    /// </summary>
    public FilesBuilder UseImageProcessor<TProcessor>() where TProcessor : class, Images.IImageProcessor
    {
        Services.AddSingleton<Images.IImageProcessor, TProcessor>();
        return this;
    }

    /// <summary>Replaces the default RavenDB attachment store. The store packages call this from their own extension.</summary>
    public FilesBuilder UseStore<TStore>() where TStore : class, IFileStore
    {
        StoreType = typeof(TStore);
        StoreFactory = null;
        return this;
    }

    /// <inheritdoc cref="UseStore{TStore}()"/>
    public FilesBuilder UseStore(Func<IServiceProvider, IFileStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        StoreFactory = factory;
        StoreType = null;
        return this;
    }
}
