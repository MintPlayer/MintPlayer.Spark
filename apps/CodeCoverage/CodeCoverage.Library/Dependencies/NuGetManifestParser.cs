using System.Xml;
using System.Xml.Linq;

namespace CodeCoverage.Dependencies;

/// <summary>
/// MSBuild project files (<c>*.csproj</c>, <c>*.fsproj</c>, <c>*.vbproj</c>) and the
/// <c>Directory.*</c> files beside them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Literal values only.</b> MSBuild is a programming language; evaluating it (imports,
/// conditions, property functions) is out of reach without a build. Any value containing
/// <c>$(</c> is ignored rather than guessed at, so the graph errs towards a missing edge, never a
/// wrong one.
/// </para>
/// <para>
/// <b>Packable</b> means: not <c>IsPackable=false</c>, not <c>IsTestProject=true</c>, no reference to
/// <c>Microsoft.NET.Test.Sdk</c>, and not a <c>Microsoft.NET.Sdk.Web</c> project unless it says
/// <c>IsPackable=true</c> (the Web SDK defaults to not packable). The produced id is
/// <c>PackageId</c>, else <c>AssemblyName</c>, else the file name — the same fallback chain
/// <c>dotnet pack</c> uses.
/// </para>
/// <para>
/// A consumed reference is <c>Dev</c> when it is private to the build (<c>PrivateAssets="all"</c>:
/// analyzers, source generators, SourceLink) or the whole project is a test project.
/// </para>
/// </remarks>
public static class NuGetManifestParser
{
    private const string TestSdk = "Microsoft.NET.Test.Sdk";

    public static ManifestParseResult ParseProject(string path, string content)
    {
        var collector = new ManifestCollector(path);
        if (!TryLoad(content, out var root, out var error)) return ManifestParseResult.Failed(error!);

        var references = PackageReferences(root!, "PackageReference").ToList();
        var isTest = IsTrue(Property(root!, "IsTestProject"))
                     || references.Any(r => string.Equals(r.Name, TestSdk, StringComparison.OrdinalIgnoreCase));

        foreach (var reference in references)
            collector.Consume(ManifestEcosystems.NuGet, reference.Name, reference.Version, isTest || reference.PrivateAll);

        var isPackable = Property(root!, "IsPackable");
        var isWeb = IsWebSdk(root!);
        var packable = !IsFalse(isPackable) && !isTest && (!isWeb || IsTrue(isPackable));
        if (packable)
        {
            var id = Property(root!, "PackageId") ?? Property(root!, "AssemblyName") ?? FileNameWithoutExtension(path);
            collector.Produce(ManifestEcosystems.NuGet, id);
        }

        return collector.Result();
    }

    /// <summary><c>Directory.Packages.props</c>, <c>Directory.Build.props|targets</c>: references only.</summary>
    public static ManifestParseResult ParseDirectoryFile(string path, string content)
    {
        var collector = new ManifestCollector(path);
        if (!TryLoad(content, out var root, out var error)) return ManifestParseResult.Failed(error!);

        foreach (var reference in PackageReferences(root!, "PackageReference").Concat(PackageReferences(root!, "GlobalPackageReference")))
            collector.Consume(ManifestEcosystems.NuGet, reference.Name, reference.Version, reference.PrivateAll);

        return collector.Result();
    }

    private sealed record Reference(string Name, string? Version, bool PrivateAll);

    private static IEnumerable<Reference> PackageReferences(XElement root, string itemName)
    {
        foreach (var item in root.Descendants().Where(e => e.Name.LocalName == itemName))
        {
            // `Update` amends a reference made elsewhere; only `Include` introduces one.
            var include = Literal(item.Attribute("Include")?.Value);
            if (include is null) continue;

            var version = Literal(item.Attribute("Version")?.Value ?? Child(item, "Version"));
            var privateAssets = item.Attribute("PrivateAssets")?.Value ?? Child(item, "PrivateAssets");
            var privateAll = privateAssets is not null
                             && privateAssets.Split(';').Any(p => string.Equals(p.Trim(), "all", StringComparison.OrdinalIgnoreCase));

            // An Include may list several packages separated by semicolons.
            foreach (var name in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return new Reference(name, version, privateAll);
        }
    }

    private static string? Child(XElement item, string name)
        => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>The first literal value of a property anywhere in the file's property groups.</summary>
    private static string? Property(XElement root, string name)
        => root.Elements().Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(g => g.Elements())
            .Where(e => e.Name.LocalName == name)
            .Select(e => Literal(e.Value))
            .FirstOrDefault(v => v is not null);

    private static bool IsWebSdk(XElement root)
    {
        var sdks = new List<string>();
        if (root.Attribute("Sdk")?.Value is { } attribute) sdks.AddRange(attribute.Split(';'));
        sdks.AddRange(root.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value ?? string.Empty));
        return sdks.Any(s => s.Trim().Split('/')[0].Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Literal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Contains("$(", StringComparison.Ordinal) ? null : value;
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    private static bool IsFalse(string? value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static string FileNameWithoutExtension(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>Parses untrusted XML with DTDs and external resolution disabled.</summary>
    private static bool TryLoad(string content, out XElement? root, out string? error)
    {
        root = null;
        error = null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(content), settings);
            root = XDocument.Load(reader).Root;
            if (root is null) error = "Empty project file.";
            return root is not null;
        }
        catch (XmlException ex)
        {
            error = $"Invalid XML: {ex.Message}";
            return false;
        }
    }
}
