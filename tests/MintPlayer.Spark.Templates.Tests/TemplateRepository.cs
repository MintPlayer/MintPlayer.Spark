using System.Text.Json.Nodes;
using System.Xml.Linq;
using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Templates.Tests;

/// <summary>Paths into this checkout, found from the test's output folder.</summary>
internal static class TemplateRepository
{
    public static readonly string Root = FindRoot();
    public static readonly string Libs = Path.Combine(Root, "libs");
    public static readonly string TemplateProject = Path.Combine(Libs, "templates", "MintPlayer.Spark.Templates");
    public static readonly string Template = Path.Combine(TemplateProject, "content", "SparkApp");

    public static FeatureManifest Manifest() => FeatureManifest.Load(Path.Combine(TemplateProject, "features.json"));

    public static string Read(string relativeToTemplate) => File.ReadAllText(Path.Combine(Template, relativeToTemplate));

    public static TemplateFiles HandWritten() => new(
        Read(".template.config/template.json"),
        Read(".template.config/ide.host.json"),
        Read(".template.config/dotnetcli.host.json"),
        Read("SparkApp/SparkApp.csproj"));

    public static JsonObject Symbols(string templateJson) => JsonNode.Parse(templateJson)!["symbols"]!.AsObject();

    public static string FullPath(string libsRelative) => Path.GetFullPath(Path.Combine(Libs, libsRelative));

    /// <summary>The direct ProjectReferences of a csproj, as full paths.</summary>
    public static IEnumerable<string> ProjectReferences(string csproj)
        => XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include!.Replace('\\', Path.DirectorySeparatorChar))));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MintPlayer.Spark.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("MintPlayer.Spark.slnx not found above the test output folder.");
    }
}
