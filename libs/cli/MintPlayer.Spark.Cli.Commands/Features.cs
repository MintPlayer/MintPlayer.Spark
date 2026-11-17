using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Cli;

/// <summary>The features.json the template was generated from, embedded at build time (one manifest, D15).</summary>
internal static class Features
{
    private static readonly Lazy<FeatureManifest> manifest = new(() =>
    {
        using var stream = typeof(Features).Assembly.GetManifestResourceStream("features.json")
            ?? throw new InvalidOperationException("The tool was built without its features.json.");
        using var reader = new StreamReader(stream);
        return FeatureManifest.Parse(reader.ReadToEnd());
    });

    public static FeatureManifest Manifest => manifest.Value;
}
