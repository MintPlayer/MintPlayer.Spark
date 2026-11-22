using Microsoft.Extensions.Configuration;

namespace MintPlayer.Spark.Files.S3;

public static class S3FilesExtensions
{
    /// <summary>
    /// Keeps file bytes in an S3-compatible bucket instead of RavenDB attachments:
    /// <code>
    /// spark.AddFiles(files =&gt; files.UseS3(o =&gt;
    /// {
    ///     o.ServiceUrl = "http://garage:3900";   // null for AWS
    ///     o.PublicUrl = "https://s3.example.com"; // what browsers reach, for direct uploads
    ///     o.Region = "garage";
    ///     o.Bucket = "myapp-files";
    ///     o.AccessKey = …; o.SecretKey = …;
    /// }));
    /// </code>
    /// </summary>
    public static FilesBuilder UseS3(this FilesBuilder files, Action<S3FileStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new S3FileStoreOptions();
        configure(options);
        var store = new S3FileStore(options);
        return files.UseStore(_ => store);
    }

    /// <summary>The same, bound from a configuration section (keys as in <see cref="S3FileStoreOptions"/>).</summary>
    public static FilesBuilder UseS3(this FilesBuilder files, IConfigurationSection section)
        => files.UseS3(section.Bind);
}
