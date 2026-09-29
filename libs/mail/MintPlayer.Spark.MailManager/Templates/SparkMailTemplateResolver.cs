using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.MailManager.Templates;

/// <summary>One layer of template files: the app's folder, or an assembly's embedded resources.</summary>
public interface ISparkMailTemplateStore
{
    /// <summary>Where the files come from, for logs and errors.</summary>
    string Description { get; }

    /// <summary>Every file, as a path relative to the layer with <c>/</c> separators (<c>SparkAuth/PasswordReset.nl.mjml</c>).</summary>
    IEnumerable<string> ListFiles();

    /// <summary>The file's text, or null when this layer has no such file.</summary>
    string? ReadFile(string path);
}

/// <summary>A template file the resolver picked.</summary>
/// <param name="Name">The template name without culture or extension (<c>SparkAuth/PasswordReset</c>).</param>
/// <param name="Path">The file's path in its layer.</param>
/// <param name="Culture">The culture of the file: <c>nl-BE</c>, <c>nl</c>, or empty for the neutral file.</param>
/// <param name="Content">The file's text.</param>
/// <param name="Source">The layer's description.</param>
public sealed record SparkMailTemplateFile(string Name, string Path, string Culture, string Content, string Source);

/// <summary>
/// Picks the template file for a recipient's culture. Replace it in DI for another scheme; the
/// default walks the culture chain — <c>{name}.nl-BE.mjml</c> → <c>{name}.nl.mjml</c> →
/// <c>{name}.mjml</c> — and, <b>per file</b>, the application's folder before embedded defaults, so an
/// app overrides a shipped template by placing a file with the same name (and culture) in its folder.
/// </summary>
public interface ISparkMailTemplateResolver
{
    /// <summary>The file for <paramref name="name"/> + <paramref name="extension"/> (<c>.mjml</c>, <c>.txt</c>) in <paramref name="culture"/>, or null.</summary>
    SparkMailTemplateFile? Resolve(string name, string extension, CultureInfo culture);

    /// <summary>Every template name that has at least one <c>.mjml</c> file (partials — file names starting with <c>_</c> — excluded).</summary>
    IReadOnlyCollection<string> TemplateNames { get; }

    /// <summary>Every file of every layer, for startup validation.</summary>
    IEnumerable<SparkMailTemplateFile> AllFiles();
}

/// <summary>The app's template folder (<c>Spark:Mail:Templates:Path</c> under the content root).</summary>
internal sealed class FileSystemMailTemplateStore(string root) : ISparkMailTemplateStore
{
    public string Description => $"folder '{root}'";

    public IEnumerable<string> ListFiles() => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
        : [];

    public string? ReadFile(string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        // An include path is template content: never read outside the folder.
        if (!full.StartsWith(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}

/// <summary>An assembly's embedded templates (<see cref="SparkMailTemplateAssembly"/>).</summary>
internal sealed class EmbeddedMailTemplateStore : ISparkMailTemplateStore
{
    private readonly Assembly assembly;
    private readonly Dictionary<string, string> resources;

    public EmbeddedMailTemplateStore(SparkMailTemplateAssembly declaration)
    {
        assembly = declaration.Assembly;
        Description = $"{declaration.Assembly.GetName().Name} resources '{declaration.ResourcePrefix}'";
        // LogicalName's %(RecursiveDir) keeps the OS separator: 'SparkMail/SparkAuth\X.mjml' when built
        // on Windows (measured, spike S-M1). Normalized so a package built anywhere reads the same.
        var prefix = declaration.ResourcePrefix.Replace('\\', '/');
        resources = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Normalized: n.Replace('\\', '/')))
            .Where(r => r.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(r => r.Normalized[prefix.Length..].TrimStart('/'), r => r.Resource, StringComparer.OrdinalIgnoreCase);
    }

    public string Description { get; }

    public IEnumerable<string> ListFiles() => resources.Keys;

    public string? ReadFile(string path)
    {
        if (!resources.TryGetValue(path, out var resource))
            return null;
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <inheritdoc />
internal sealed partial class SparkMailTemplateResolver : ISparkMailTemplateResolver
{
    private readonly IReadOnlyList<ISparkMailTemplateStore> stores;
    private readonly ILogger<SparkMailTemplateResolver> logger;

    public SparkMailTemplateResolver(
        IOptions<SparkMailOptions> options,
        IHostEnvironment environment,
        IEnumerable<SparkMailTemplateAssembly> embedded,
        IEnumerable<ISparkMailTemplateStore> extra,
        ILogger<SparkMailTemplateResolver> logger)
    {
        this.logger = logger;
        var folder = System.IO.Path.Combine(environment.ContentRootPath, options.Value.Templates.Path);
        // Order is precedence: the app's folder, stores an app registered itself, then shipped defaults.
        stores = [new FileSystemMailTemplateStore(folder), .. extra, .. embedded.Select(e => new EmbeddedMailTemplateStore(e))];
    }

    /// <summary>For tests and the render helper: explicit layers, first wins.</summary>
    internal SparkMailTemplateResolver(IReadOnlyList<ISparkMailTemplateStore> stores, ILogger<SparkMailTemplateResolver> logger)
    {
        this.stores = stores;
        this.logger = logger;
    }

    public IReadOnlyCollection<string> TemplateNames => [.. AllFiles()
        .Where(f => f.Path.EndsWith(".mjml", StringComparison.OrdinalIgnoreCase) && !System.IO.Path.GetFileName(f.Name).StartsWith('_'))
        .Select(f => f.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];

    public IEnumerable<SparkMailTemplateFile> AllFiles()
    {
        foreach (var store in stores)
            foreach (var path in store.ListFiles())
            {
                var (name, culture, _) = Split(path);
                yield return new SparkMailTemplateFile(name, path, culture, store.ReadFile(path) ?? string.Empty, store.Description);
            }
    }

    public SparkMailTemplateFile? Resolve(string name, string extension, CultureInfo culture)
    {
        var chain = CultureChain(culture);
        foreach (var candidate in chain)
        {
            var path = candidate.Length == 0 ? $"{name}{extension}" : $"{name}.{candidate}{extension}";
            foreach (var store in stores)
            {
                if (store.ReadFile(path) is { } content)
                {
                    if (candidate != chain[0])
                        logger.LogDebug("Mail template {Template}{Extension}: no {Culture} file, using {Path} from {Source}.", name, extension, culture.Name, path, store.Description);
                    return new SparkMailTemplateFile(name, path, candidate, content, store.Description);
                }
            }
        }
        return null;
    }

    /// <summary><c>nl-BE</c> → <c>["nl-BE", "nl", ""]</c>; the invariant culture → <c>[""]</c>.</summary>
    internal static IReadOnlyList<string> CultureChain(CultureInfo culture)
    {
        var chain = new List<string>();
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            chain.Add(c.Name);
        chain.Add(string.Empty);
        return chain;
    }

    /// <summary><c>A/B.nl-BE.mjml</c> → (<c>A/B</c>, <c>nl-BE</c>, <c>.mjml</c>); a last segment that is no culture stays in the name.</summary>
    internal static (string Name, string Culture, string Extension) Split(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        var stem = path[..^extension.Length];
        var dot = stem.LastIndexOf('.');
        var slash = stem.LastIndexOf('/');
        if (dot > slash && dot > 0 && IsCulture(stem[(dot + 1)..]))
            return (stem[..dot], stem[(dot + 1)..], extension);
        return (stem, string.Empty, extension);
    }

    // A shape test, not a CultureInfo lookup: the answer must not change with the host's ICU data or
    // InvariantGlobalization (a container image), or startup validation would read a file differently
    // than it does on the developer's machine.
    private static bool IsCulture(string candidate) => CultureSuffix().IsMatch(candidate);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8}){0,2}$")]
    private static partial System.Text.RegularExpressions.Regex CultureSuffix();
}
