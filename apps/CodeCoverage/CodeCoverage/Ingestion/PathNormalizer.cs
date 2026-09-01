namespace CodeCoverage.Ingestion;

/// <summary>
/// Turns the file paths coverage reports contain (absolute CI paths, paths
/// relative to unstated source roots, backslashes…) into repo-relative
/// forward-slash paths, using the uploader-provided context: the workspace
/// root and the repo file list (`git ls-files`). Unresolvable paths are
/// returned unmatched rather than dropped, so the UI can surface them.
/// </summary>
public sealed class PathNormalizer
{
    private readonly string? rootDir;
    private readonly string[] sourceRoots;
    private readonly HashSet<string> fileList;
    private readonly ILookup<string, string>? fileListBySuffix;

    public PathNormalizer(string? rootDir, string[] sourceRoots, IReadOnlyCollection<string> fileList)
    {
        this.rootDir = rootDir is null ? null : Unify(rootDir).TrimEnd('/') + "/";
        this.sourceRoots = sourceRoots.Select(r => Unify(r).TrimEnd('/') + "/").ToArray();
        this.fileList = fileList.Select(Unify).ToHashSet(StringComparer.Ordinal);
        this.fileListBySuffix = this.fileList.ToLookup(f => f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    public (string Path, bool Matched) Normalize(string rawPath)
    {
        var path = Unify(rawPath);

        // 1. Strip the workspace root (absolute CI paths).
        if (rootDir is not null && path.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            path = path[rootDir.Length..];

        // 2. Strip a report-declared source root, absolute or relative.
        foreach (var root in sourceRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path[root.Length..];
                break;
            }
        }

        var stillAbsolute = LooksAbsolute(path);
        path = path.TrimStart('/');

        // 3. Without a file list we can only trust root-stripped relative paths.
        if (fileList.Count == 0)
            return (path, Matched: !stillAbsolute);

        if (fileList.Contains(path))
            return (path, true);

        // Case-insensitive exact (Windows-built reports vs POSIX tree).
        var ciMatch = fileList.FirstOrDefault(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (ciMatch is not null)
            return (ciMatch, true);

        // 4. Longest-suffix match: unique repo file whose path ends with the
        //    report path's tail (handles unstated source roots like src/main/java).
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var candidates = fileListBySuffix![fileName]
            .Where(f => EndsWithPath(f, path) || EndsWithPath(path, f))
            .ToList();
        if (candidates.Count == 1)
            return (candidates[0], true);

        return (path, false);
    }

    private static bool EndsWithPath(string full, string tail)
        => full.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
           && (full.Length == tail.Length || full[full.Length - tail.Length - 1] == '/');

    private static bool LooksAbsolute(string path)
        => path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

    private static string Unify(string path) => path.Replace('\\', '/');
}
