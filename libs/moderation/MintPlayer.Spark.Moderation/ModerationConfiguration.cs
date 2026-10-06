using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// <c>moderation.json</c> as an <see cref="IConfiguration"/> source (#460, D14).
/// </summary>
/// <remarks>
/// <para>
/// The file holds the <em>contents</em> of the <c>Spark:Moderation</c> section — its keys are
/// prefixed on load — and is inserted as the <b>first</b> (lowest-precedence) source, so appsettings,
/// user secrets, environment variables and the command line all override it. An operator raises a
/// cap with <c>Spark__Moderation__Fraud__MaxVotesCastPerDay=10</c> without touching the file.
/// </para>
/// <para>
/// Validation runs on the <b>layered</b> result, at startup, not on the file.
/// </para>
/// </remarks>
public static class SparkModerationConfigurationExtensions
{
    /// <summary>The default location, beside <c>security.json</c> in the application's <see cref="SparkAppData"/> directory.</summary>
    public static string DefaultPath => SparkAppData.Relative("moderation.json");

    /// <summary>The configuration section the file is mounted under.</summary>
    public const string SectionName = "Spark:Moderation";

    /// <summary>
    /// Adds <paramref name="path"/> (relative to the builder's base path — the content root in an
    /// ASP.NET Core host) as the lowest-precedence source, mounted under <c>Spark:Moderation</c>.
    /// Idempotent: a second call for the same path adds nothing. Defaults to <see cref="DefaultPath"/>;
    /// an absolute path is read from where it points.
    /// </summary>
    public static IConfigurationBuilder AddSparkModerationFile(this IConfigurationBuilder builder, string? path = null, bool optional = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        path ??= DefaultPath;
        if (builder.Sources.OfType<ModerationJsonConfigurationSource>().Any(s => string.Equals(s.RequestedPath, path, StringComparison.OrdinalIgnoreCase)))
            return builder;

        var rooted = System.IO.Path.IsPathRooted(path);
        var source = new ModerationJsonConfigurationSource
        {
            RequestedPath = path,
            Path = path,
            Optional = optional,
            ReloadOnChange = false,
            // The builder's provider is rooted at the base path and cannot serve an absolute path;
            // ResolveFileProvider gives it one rooted at the file's own directory instead.
            FileProvider = rooted ? null : builder.GetFileProvider(),
        };
        if (rooted) source.ResolveFileProvider();
        builder.Sources.Insert(0, source);
        return builder;
    }
}

/// <summary>A JSON file whose keys are mounted under <c>Spark:Moderation</c>.</summary>
internal sealed class ModerationJsonConfigurationSource : JsonConfigurationSource
{
    /// <summary>The path as passed in, before <see cref="FileConfigurationSource.ResolveFileProvider"/> shortens it.</summary>
    public string? RequestedPath { get; init; }

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new Provider(this);
    }

    private sealed class Provider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        public override void Load(Stream stream)
        {
            base.Load(stream);
            var prefixed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in Data)
                prefixed[SparkModerationConfigurationExtensions.SectionName + ConfigurationPath.KeyDelimiter + key] = value;
            Data = prefixed;
        }
    }
}
