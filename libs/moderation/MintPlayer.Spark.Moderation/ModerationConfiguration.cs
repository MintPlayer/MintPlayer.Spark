using System.Text;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// <c>moderation.json</c>, composed in layers, as an <see cref="IConfiguration"/> source (#460, D14;
/// composition D3, grill Q6).
/// </summary>
/// <remarks>
/// <para>
/// The library ships its defaults (the reputation table and the privileges, each conferring a slot)
/// as a layer; the application's file composes on top per key (<see cref="SparkModerationFiles"/>), and
/// <c>"Review": null</c> removes a privilege. The result holds the <em>contents</em> of the
/// <c>Spark:Moderation</c> section — its keys are prefixed on load — and is inserted as the <b>first</b>
/// (lowest-precedence) source, so appsettings, user secrets, environment variables and the command
/// line all override it. An operator raises a cap with
/// <c>Spark__Moderation__Fraud__MaxVotesCastPerDay=10</c> without touching the file.
/// </para>
/// <para>
/// Validation runs on the <b>layered</b> result, at startup, not on the file. A privilege's slot
/// resolves through <c>security.json</c>'s <c>bindings</c> then.
/// </para>
/// </remarks>
public static class SparkModerationConfigurationExtensions
{
    /// <summary>The default location, beside <c>security.json</c> in the application's <see cref="SparkAppData"/> directory.</summary>
    public static string DefaultPath => SparkAppData.Relative("moderation.json");

    /// <summary>The configuration section the file is mounted under.</summary>
    public const string SectionName = "Spark:Moderation";

    /// <summary>
    /// Adds the library defaults composed with <paramref name="path"/> (relative to the builder's base
    /// path — the content root in an ASP.NET Core host) as the lowest-precedence source, mounted under
    /// <c>Spark:Moderation</c>. Idempotent: a second call for the same path adds nothing. Defaults to
    /// <see cref="DefaultPath"/>; an absolute path is read from where it points. Without the file, the
    /// library defaults alone.
    /// </summary>
    public static IConfigurationBuilder AddSparkModerationFile(this IConfigurationBuilder builder, string? path = null, bool optional = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        path ??= DefaultPath;
        if (builder.Sources.OfType<ModerationConfigurationSource>().Any(s => string.Equals(s.RequestedPath, path, StringComparison.OrdinalIgnoreCase)))
            return builder;

        // The builder's file provider is rooted at the base path, and cannot serve an absolute path.
        var physical = System.IO.Path.IsPathRooted(path)
            ? path
            : builder.GetFileProvider().GetFileInfo(path).PhysicalPath ?? System.IO.Path.GetFullPath(path);

        builder.Sources.Insert(0, new ModerationConfigurationSource(path, physical, optional));
        return builder;
    }
}

/// <summary>The library layers and the application's <c>moderation.json</c>, composed, mounted under <c>Spark:Moderation</c>.</summary>
internal sealed class ModerationConfigurationSource(string requestedPath, string physicalPath, bool optional) : IConfigurationSource
{
    /// <summary>The path as passed in, for idempotence.</summary>
    public string RequestedPath { get; } = requestedPath;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(physicalPath, optional);

    private sealed class Provider(string path, bool optional) : ConfigurationProvider
    {
        public override void Load()
        {
            string? appJson = null;
            if (File.Exists(path))
                appJson = File.ReadAllText(path);
            else if (!optional)
                throw new FileNotFoundException($"The moderation configuration '{path}' was not found and is not optional.", path);

            var composed = SparkModerationFiles.Compose(appJson, path);

            // The composed tree goes through the JSON configuration reader, so arrays and values
            // flatten exactly as they did when the file was read directly.
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(composed));
            var flattened = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in flattened.AsEnumerable())
            {
                if (value is not null)
                    data[SparkModerationConfigurationExtensions.SectionName + ConfigurationPath.KeyDelimiter + key] = value;
            }
            Data = data;
        }
    }
}
