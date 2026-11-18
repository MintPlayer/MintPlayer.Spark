using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Cli;

/// <summary>
/// Where <c>spark add</c> writes: the app (the project with <c>App_Data</c>), the entity library it references (the
/// spark-app layout's <c>*.Library</c> project, or the app itself when it has none) and the context class.
/// </summary>
internal sealed partial record SparkProject(string AppDirectory, string AppProject, string LibraryDirectory, string? ContextFile)
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", "ClientApp", "App_Data"];

    [GeneratedRegex("""<ProjectReference\s+Include="([^"]+\.Library\.csproj)"\s*/?>""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryReference();

    public string SecurityFile => Path.Combine(AppDirectory, "App_Data", "security.json");

    public string ActionsFile => Path.Combine(AppDirectory, "App_Data", "actions.json");

    /// <summary>
    /// Walks up from <paramref name="start"/>: the first app directory wins; a solution directory on the way picks its
    /// one app subdirectory. Returns null outside a Spark app; refuses a solution with more than one app.
    /// </summary>
    public static SparkProject? Locate(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if (IsApp(directory.FullName)) return Load(directory.FullName);
            if (!directory.EnumerateFiles("*.sln").Any() && !directory.EnumerateFiles("*.slnx").Any()) continue;

            var apps = directory.EnumerateDirectories().Where(d => IsApp(d.FullName)).ToList();
            return apps.Count switch
            {
                0 => null,
                1 => Load(apps[0].FullName),
                _ => throw new InvalidOperationException(
                    $"The solution holds more than one Spark app ({string.Join(", ", apps.Select(a => a.Name))}); run the command in one of them."),
            };
        }
        return null;
    }

    private static bool IsApp(string directory)
        => Directory.Exists(Path.Combine(directory, "App_Data")) && Directory.EnumerateFiles(directory, "*.csproj").Any();

    private static SparkProject Load(string appDirectory)
    {
        var appProject = Directory.EnumerateFiles(appDirectory, "*.csproj").First();
        var reference = LibraryReference().Match(File.ReadAllText(appProject));
        var libraryDirectory = reference.Success
            ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(appDirectory, reference.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))))!
            : appDirectory;
        return new SparkProject(appDirectory, appProject, libraryDirectory, FindContext(appDirectory));
    }

    private static string? FindContext(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            if (ContextEditor.IsContext(File.ReadAllText(file))) return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
            if (FindContext(child) is { } found) return found;
        }
        return null;
    }
}
