namespace CodeCoverage.Dependencies;

/// <summary>The kinds of manifest the scanner reads.</summary>
public enum EManifestKind
{
    None,
    NpmPackageJson,
    MsBuildProject,
    MsBuildDirectoryFile,
    PipRequirements,
    PyProject,
    ComposerJson,
    ActionsWorkflow,
    Dockerfile,
    Compose,
}

/// <summary>
/// Which paths in a repository tree are manifests, and the one entry point that parses any of them.
/// Pure: no I/O, never throws.
/// </summary>
public static class ManifestFiles
{
    /// <summary>
    /// Path segments never scanned: installed or vendored third-party code and build output. A
    /// <c>package.json</c> under <c>node_modules/</c> describes somebody else's package, and counting
    /// it would make every repository "consume" its whole lockfile.
    /// </summary>
    private static readonly HashSet<string> SkippedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "bin", "obj", "dist", ".git",
    };

    /// <summary>True when <paramref name="path"/> lies under a skipped directory.</summary>
    public static bool IsSkippedPath(string path)
    {
        var segments = path.Split('/');
        // The last segment is the file name, which is never itself a skipped directory.
        for (var i = 0; i < segments.Length - 1; i++)
            if (SkippedSegments.Contains(segments[i])) return true;
        return false;
    }

    /// <summary>What kind of manifest <paramref name="path"/> is, ignoring skipped directories.</summary>
    public static EManifestKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return EManifestKind.None;

        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        var directory = slash < 0 ? string.Empty : path[..slash];

        if (Is(name, "package.json")) return EManifestKind.NpmPackageJson;
        if (Ends(name, ".csproj") || Ends(name, ".fsproj") || Ends(name, ".vbproj")) return EManifestKind.MsBuildProject;
        if (Is(name, "Directory.Packages.props") || Is(name, "Directory.Build.props") || Is(name, "Directory.Build.targets"))
            return EManifestKind.MsBuildDirectoryFile;
        if (Starts(name, "requirements") && Ends(name, ".txt")) return EManifestKind.PipRequirements;
        if (Is(name, "pyproject.toml")) return EManifestKind.PyProject;
        if (Is(name, "composer.json")) return EManifestKind.ComposerJson;

        var yaml = Ends(name, ".yml") || Ends(name, ".yaml");
        // GitHub runs only the direct children of .github/workflows.
        if (yaml && Is(directory, ".github/workflows")) return EManifestKind.ActionsWorkflow;
        if (Is(name, "action.yml") || Is(name, "action.yaml")) return EManifestKind.ActionsWorkflow;
        if (Starts(name, "Dockerfile")) return EManifestKind.Dockerfile;
        if (yaml && (Starts(name, "docker-compose") || Starts(name, "compose"))) return EManifestKind.Compose;

        return EManifestKind.None;
    }

    /// <summary>True when the scanner should read <paramref name="path"/>.</summary>
    public static bool IsManifest(string path) => !IsSkippedPath(path) && Classify(path) != EManifestKind.None;

    /// <summary>Parses one manifest. Unknown kinds yield an empty result.</summary>
    public static ManifestParseResult Parse(string path, string content)
    {
        try
        {
            content = content.TrimStart('﻿');
            return Classify(path) switch
            {
                EManifestKind.NpmPackageJson => NpmManifestParser.Parse(path, content),
                EManifestKind.MsBuildProject => NuGetManifestParser.ParseProject(path, content),
                EManifestKind.MsBuildDirectoryFile => NuGetManifestParser.ParseDirectoryFile(path, content),
                EManifestKind.PipRequirements => PipManifestParser.ParseRequirements(path, content),
                EManifestKind.PyProject => PipManifestParser.ParsePyProject(path, content),
                EManifestKind.ComposerJson => ComposerManifestParser.Parse(path, content),
                EManifestKind.ActionsWorkflow => ActionsManifestParser.Parse(path, content),
                EManifestKind.Dockerfile => DockerManifestParser.ParseDockerfile(path, content),
                EManifestKind.Compose => DockerManifestParser.ParseCompose(path, content),
                _ => ManifestParseResult.Empty,
            };
        }
        catch (Exception ex)
        {
            // Each parser already catches its own format errors; this is the backstop for a bug in
            // one, so a single file can never fail a whole repository's scan.
            return ManifestParseResult.Failed(ex.Message);
        }
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    private static bool Ends(string value, string suffix) => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    private static bool Starts(string value, string prefix) => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
