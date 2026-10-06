using System.Reflection;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Where the application keeps its Spark configuration: the <c>SparkAppDataDir</c> MSBuild property
/// (default <c>App_Data</c>), resolved against the content root.
/// </summary>
/// <remarks>
/// The build stamps the property into the application assembly as
/// <c>[assembly: AssemblyMetadata("SparkAppDataDir", ...)]</c> (<c>spark.targets</c>), the same value
/// that selects the <c>AdditionalFiles</c> the generators read, so build, generators and runtime agree
/// on one directory (composition PRD D12). Read once from the entry assembly. A host whose entry
/// assembly carries no such attribute (a test runner, a tool) gets <c>App_Data</c>.
/// <para>
/// A relative value is combined with the content root; an absolute value is used as-is.
/// </para>
/// </remarks>
public static class SparkAppData
{
    /// <summary>The <see cref="AssemblyMetadataAttribute"/> key the build writes.</summary>
    public const string MetadataKey = "SparkAppDataDir";

    /// <summary>The directory used when nothing is configured.</summary>
    public const string DefaultDirectory = "App_Data";

    private static readonly Lazy<string> configured = new(() => Configured(Assembly.GetEntryAssembly()));

    /// <summary>
    /// The configured directory as messages name it, with forward slashes: <c>App_Data</c> unless the
    /// application moved it. Absolute when the application configured an absolute directory.
    /// </summary>
    public static string RelativeDirectory => configured.Value;

    /// <summary>The configuration directory for <paramref name="contentRootPath"/>.</summary>
    public static string Directory(string contentRootPath)
        => Resolve(contentRootPath, configured.Value);

    /// <summary>A path inside the configuration directory, e.g. <c>Path(root, "Model")</c>.</summary>
    public static string Path(string contentRootPath, params string[] parts)
        => System.IO.Path.Combine([Directory(contentRootPath), .. parts]);

    /// <summary>A file inside the configuration directory as messages name it, e.g. <c>App_Data/security.json</c>.</summary>
    public static string Relative(params string[] parts)
        => string.Join("/", [RelativeDirectory, .. parts]);

    /// <summary>
    /// The value <paramref name="assembly"/> was built with, normalised to forward slashes without a
    /// trailing separator; <see cref="DefaultDirectory"/> when the assembly is null or carries none.
    /// </summary>
    internal static string Configured(Assembly? assembly)
    {
        var value = assembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))?
            .Value;
        return Normalize(value);
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultDirectory;
        var normalized = value.Trim().Replace('\\', '/');
        // Keep a bare root ("/") intact; strip the separator from everything else.
        normalized = normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        return normalized.Length == 0 ? DefaultDirectory : normalized;
    }

    internal static string Resolve(string contentRootPath, string configuredDirectory)
    {
        var native = configuredDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.IsPathRooted(native)
            ? native
            : System.IO.Path.Combine(contentRootPath, native);
    }
}
