namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// The code-defined file rules of the <c>dependencies</c> profile (docs/code-coverage/dependency-updates-PRD.md gap A):
/// which file names may hold a record of a datasource, which lockfiles belong to it, and the deny-list that wins over both.
/// The lookup report only <i>selects</i> among these; it can never widen them (§13.11.3 #6).
/// </summary>
public static class DependencyFiles
{
    /// <summary>The bot's plan file on the dispatch ref (R14). Every dependency profile refuses it, and the whole
    /// <c>.mintplayer/</c> directory, which only the bot writes.</summary>
    public const string PlanPath = ".mintplayer/dependency-plan.json";

    private sealed record Rule(Func<string, string, bool> Manifest, string[] Lockfiles);

    private static bool Name(string name, params string[] names) => names.Any(n => name.Equals(n, StringComparison.Ordinal));

    private static bool Ext(string name, params string[] extensions) => extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase) && name.Length > e.Length);

    private static readonly string[] JsLockfiles = ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock"];
    private static readonly string[] PythonLockfiles = ["poetry.lock", "uv.lock", "Pipfile.lock", "pdm.lock"];

    /// <summary>Per datasource (D-S12d): (file name, path) → may hold the record; and its lockfile names. S12's reports
    /// (scratchpad spikes/s12/fixtures/renovate) list each pairing below.</summary>
    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["npm"] = new((n, _) => Name(n, "package.json"), JsLockfiles),
        ["nuget"] = new((n, p) => Ext(n, ".csproj", ".fsproj", ".vbproj") || Name(n, "Directory.Packages.props", "packages.config")
                                  || n == "dotnet-tools.json" && (p == ".config/dotnet-tools.json" || p == "dotnet-tools.json" || p.EndsWith("/.config/dotnet-tools.json", StringComparison.Ordinal)),
            ["packages.lock.json"]),
        ["dotnet-version"] = new((n, _) => Name(n, "global.json"), []),
        ["node-version"] = new((n, _) => Name(n, "package.json", ".nvmrc", ".node-version", ".tool-versions"), []),
        ["maven"] = new((n, _) => Name(n, "pom.xml", "build.gradle", "build.gradle.kts") || n.EndsWith(".versions.toml", StringComparison.Ordinal), ["gradle.lockfile"]),
        ["crate"] = new((n, _) => Name(n, "Cargo.toml"), ["Cargo.lock"]),
        ["go"] = new((n, _) => Name(n, "go.mod"), ["go.sum"]),
        ["golang-version"] = new((n, _) => Name(n, "go.mod"), []),
        ["pypi"] = new((n, _) => Name(n, "pyproject.toml", "Pipfile", "setup.cfg")
                                 || (n.StartsWith("requirements", StringComparison.Ordinal) && (n.EndsWith(".txt", StringComparison.Ordinal) || n.EndsWith(".in", StringComparison.Ordinal))),
            PythonLockfiles),
        ["python-version"] = new((n, _) => Name(n, "pyproject.toml", ".python-version", ".tool-versions"), PythonLockfiles),
        ["packagist"] = new((n, _) => Name(n, "composer.json"), ["composer.lock"]),
        ["rubygems"] = new((n, _) => Name(n, "Gemfile"), ["Gemfile.lock"]),
        ["docker"] = new((n, _) => Name(n, "Dockerfile", ".python-version") || n.EndsWith(".Dockerfile", StringComparison.Ordinal) || n.StartsWith("Dockerfile.", StringComparison.Ordinal)
                                   || ((n.StartsWith("docker-compose", StringComparison.Ordinal) || n.StartsWith("compose", StringComparison.Ordinal))
                                       && (n.EndsWith(".yml", StringComparison.Ordinal) || n.EndsWith(".yaml", StringComparison.Ordinal))), []),
        ["rust-version"] = new((n, _) => Name(n, "rust-toolchain.toml", "rust-toolchain"), []),
        ["java-version"] = new((n, _) => Name(n, ".tool-versions"), []),
        ["github-tags"] = new((n, _) => Name(n, ".tool-versions"), []),
        ["github-releases"] = new((n, _) => Name(n, ".tool-versions"), []),
    };

    /// <summary>Every lockfile name of every datasource.</summary>
    public static IReadOnlySet<string> LockfileNames { get; } = Rules.Values.SelectMany(r => r.Lockfiles).ToHashSet(StringComparer.Ordinal);

    /// <summary>The file named by a record of <paramref name="datasource"/> may hold it. Paths under <c>.github/</c> are
    /// never manifests: the workflow carve-out decides on them.</summary>
    public static bool IsManifest(string datasource, string path)
        => Rules.TryGetValue(datasource, out var rule) && !PatchValidator.IsUnderGitHub(path) && rule.Manifest(FileName(path), path);

    /// <summary><paramref name="lockfile"/> is a code-defined lockfile of <paramref name="datasource"/> in the package
    /// file's directory or one of its ancestors (a workspace root holds the lockfile of its members).</summary>
    public static bool IsLockfileOf(string datasource, string packageFile, string lockfile)
    {
        if (!Rules.TryGetValue(datasource, out var rule) || !rule.Lockfiles.Contains(FileName(lockfile), StringComparer.Ordinal))
            return false;
        var lockDirectory = Directory(lockfile);
        var directory = Directory(packageFile);
        if (lockDirectory.Length == 0 || directory == lockDirectory || directory.StartsWith(lockDirectory + "/", StringComparison.Ordinal))
            return true;
        // Central package management: Directory.Packages.props versions every project below it, each with its own
        // packages.lock.json beside it.
        return FileName(packageFile) == "Directory.Packages.props" && (directory.Length == 0 || lockDirectory.StartsWith(directory + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The deny-list (gap A), case-insensitive because the file systems that fold case would read these too: MSBuild
    /// files that run on every build (<c>Directory.Build.*</c>, <c>Directory.Solution.*</c>, any <c>*.targets</c> and any
    /// <c>*.props</c> but <c>Directory.Packages.props</c>), feed and install configuration (<c>nuget.config</c>,
    /// <c>.npmrc</c>, <c>.yarnrc[.yml]</c> with its <c>yarnPath</c>, <c>.pnpmfile.*</c>, <c>bunfig.toml</c>), build
    /// scripts that are not manifests (<c>setup.py</c>, Gradle settings, init scripts and properties, the Gradle and Maven
    /// wrappers), and the <c>.mvn</c>, <c>.yarn</c> and <c>.husky</c> directories. The bot's plan path and
    /// <c>coverage.yml</c> are refused by every profile before this.
    /// </summary>
    public static bool IsDenied(string path)
    {
        var components = path.Split('/');
        if (components.Any(c => c.Equals(".mvn", StringComparison.OrdinalIgnoreCase) || c.Equals(".yarn", StringComparison.OrdinalIgnoreCase)
                                || c.Equals(".husky", StringComparison.OrdinalIgnoreCase)))
            return true;
        var name = components[^1].ToLowerInvariant();
        if (name.StartsWith("directory.build.", StringComparison.Ordinal) || name.StartsWith("directory.solution.", StringComparison.Ordinal))
            return true;
        if (name.EndsWith(".targets", StringComparison.Ordinal) || name.EndsWith(".props", StringComparison.Ordinal) && name != "directory.packages.props")
            return true;
        return name is "nuget.config" or ".npmrc" or ".yarnrc" or ".yarnrc.yml" or ".pnpmfile.cjs" or ".pnpmfile.mjs" or ".pnpmfile.js" or "bunfig.toml"
            or "setup.py" or "settings.gradle" or "settings.gradle.kts" or "init.gradle" or "init.gradle.kts" or "gradle.properties"
            or "gradle-wrapper.properties" or "maven-wrapper.properties" or "build.rs"
            || name.EndsWith(".init.gradle", StringComparison.Ordinal) || name.EndsWith(".init.gradle.kts", StringComparison.Ordinal);
    }

    /// <summary>The bot's own files: the plan path and everything under <c>.mintplayer/</c> (R14).</summary>
    public static bool IsPlanPath(string path) => path.StartsWith(".mintplayer/", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>coverage.yml</c> at the root, the configuration the bot reads (R9/R18).</summary>
    public static bool IsCoverageConfig(string path) => path.Equals("coverage.yml", StringComparison.OrdinalIgnoreCase) || path.Equals("coverage.yaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>A workflow GitHub runs (<c>.github/workflows/*.yml|yaml</c>, directly in the directory), or an
    /// <c>action.yml</c>/<c>action.yaml</c> anywhere (a composite action): the R15 carve-out's files.</summary>
    public static bool IsWorkflowFile(string path)
    {
        var name = FileName(path);
        if (path.StartsWith(".github/workflows/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2)
            return name.EndsWith(".yml", StringComparison.Ordinal) || name.EndsWith(".yaml", StringComparison.Ordinal);
        return name is "action.yml" or "action.yaml";
    }

    /// <summary>The datasources whose records may live in a workflow or <c>action.yml</c> (S12): Actions refs, and the
    /// version inputs of the closed setup-action list. A record of any other datasource there (an npm record claiming
    /// <c>.github/workflows/ci.yml</c>) widens nothing (§13.11.3 #6).</summary>
    public static IReadOnlySet<string> WorkflowDatasources { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "github-tags", "github-digest", "github-releases", "dotnet-version", "node-version", "python-version", "java-version", "golang-version",
        "ruby-version",
    };

    /// <summary>The file is under <c>.github/workflows/</c>, which the writer's token can change only with
    /// <c>workflows: write</c>.</summary>
    public static bool NeedsWorkflowsPermission(string path) => path.StartsWith(".github/workflows/", StringComparison.Ordinal);

    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Directory(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;
}
