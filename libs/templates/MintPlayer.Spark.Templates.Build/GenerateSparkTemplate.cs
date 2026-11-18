using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace MintPlayer.Spark.Templates.Build;

/// <summary>
/// MSBuild task: completes the spark-app template from features.json into <see cref="OutputDirectory"/>, and lists the
/// Spark projects whose versions the template pins.
/// </summary>
public sealed class GenerateSparkTemplate : Task
{
    private const string TemplateJson = ".template.config/template.json";
    private const string IdeHostJson = ".template.config/ide.host.json";
    private const string CliHostJson = ".template.config/dotnetcli.host.json";
    private const string HostCsproj = "SparkApp/SparkApp.csproj";

    /// <summary>features.json.</summary>
    [Required] public string ManifestFile { get; set; } = "";

    /// <summary>The <c>libs/</c> folder the manifest's project paths are relative to.</summary>
    [Required] public string LibsDirectory { get; set; } = "";

    /// <summary>The template's root: content/SparkApp.</summary>
    [Required] public string TemplateDirectory { get; set; } = "";

    [Required] public string OutputDirectory { get; set; } = "";

    /// <summary>The generated files; <c>RelativePath</c> is the path under the template root.</summary>
    [Output] public ITaskItem[] GeneratedFiles { get; private set; } = [];

    /// <summary>
    /// The projects whose versions the template pins: core, features, and the "dependency" packages a hand-written
    /// file may reference directly (the Library csproj takes Moderation.Abstractions for the moderatable Person).
    /// </summary>
    [Output] public ITaskItem[] Packages { get; private set; } = [];

    public override bool Execute()
    {
        try
        {
            var manifest = FeatureManifest.Load(ManifestFile);
            var hand = new TemplateFiles(Read(TemplateJson), Read(IdeHostJson), Read(CliHostJson), Read(HostCsproj));
            var generated = SparkTemplateGenerator.Generate(manifest, hand);

            GeneratedFiles =
            [
                Write(TemplateJson, generated.TemplateJson),
                Write(IdeHostJson, generated.IdeHostJson),
                Write(CliHostJson, generated.CliHostJson),
                Write(HostCsproj, generated.HostCsproj),
            ];
            Packages = manifest.Core.Concat<ManifestProject>(manifest.Features)
                .Concat(manifest.Excluded.Where(e => e.Reason == "dependency"))
                .Select(p => (ITaskItem)new TaskItem(Path.GetFullPath(Path.Combine(LibsDirectory, p.Project))))
                .ToArray();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Log.LogError(null, "SPARKTPL002", null, ManifestFile, 0, 0, 0, 0, "features.json: {0}", ex.Message);
            return false;
        }
    }

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(TemplateDirectory, relativePath));

    private ITaskItem Write(string relativePath, string content)
    {
        var path = Path.GetFullPath(Path.Combine(OutputDirectory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
        var item = new TaskItem(path);
        item.SetMetadata("RelativePath", relativePath.Replace('/', '\\'));
        return item;
    }
}
