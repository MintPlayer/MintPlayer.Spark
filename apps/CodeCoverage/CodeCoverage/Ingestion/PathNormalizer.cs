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
    private readonly Dictionary<string, string> fileListLiteral;
    private readonly ILookup<string, string>? fileListBySuffix;

    public PathNormalizer(string? rootDir, string[] sourceRoots, IReadOnlyCollection<string> fileList)
    {
        this.rootDir = rootDir is null ? null : Unify(rootDir).TrimEnd('/') + "/";
        this.sourceRoots = sourceRoots.Select(r => Unify(r).TrimEnd('/') + "/").ToArray();

        // The list as git reported it, before unification — the only way to tell a
        // repository that genuinely contains `src/weird\name.cs` from one that
        // contains `src/weird/name.cs`. Unifying is lossy, so both forms are kept.
        // Last-wins on a collision is fine: the two entries are indistinguishable
        // after unification anyway, which is precisely the ambiguity this avoids.
        this.fileListLiteral = fileList
            .Where(f => f.Contains('\\', StringComparison.Ordinal))
            .GroupBy(f => f, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Key, StringComparer.Ordinal);

        this.fileList = fileList.Select(Unify).ToHashSet(StringComparer.Ordinal);
        this.fileListBySuffix = this.fileList.ToLookup(f => f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a report-supplied path, trying it <b>as received</b> before trying it
    /// with separators unified.
    ///
    /// <para>The ordering matters for exactly one case, raised on issue #417: a
    /// backslash is a legal character in a POSIX filename, so <c>src/weird\name.cs</c>
    /// can genuinely be committed. Unifying first would collide it with
    /// <c>src/weird/name.cs</c> and resolve to the wrong file — silently, because both
    /// are "matched". Trying the literal path first means the common Windows case is
    /// unaffected (it has no literal match and falls through to the unified pass)
    /// while a genuinely backslash-bearing POSIX file resolves to itself.</para>
    ///
    /// <para>The literal pass is deliberately an <b>exact</b> file-list match and
    /// nothing more. A Windows absolute path like <c>D:\a\repo\repo\src\File.cs</c>
    /// cannot match the repo's file list literally, so it falls straight through to
    /// the unified pipeline that has always handled it — the common case is
    /// untouched, by construction rather than by luck.</para>
    ///
    /// <para>This is not fixing an observed failure. The #415 A/B proved separators
    /// were never the cause — the same absolute backslash paths resolved all 79 files
    /// once the BOM was gone. It makes existing behaviour explicit and pins it.</para>
    /// </summary>
    public (string Path, bool Matched) Normalize(string rawPath)
    {
        // Only worth a literal pass when the two forms actually differ, and only
        // against paths the repository genuinely contains in that exact shape.
        if (rawPath.Contains('\\', StringComparison.Ordinal)
            && fileListLiteral.TryGetValue(rawPath.TrimStart('/'), out var literal))
        {
            return (literal, true);
        }

        var path = Unify(rawPath);
        var stripped = false;

        // 1. Strip the workspace root (absolute CI paths).
        if (rootDir is not null && path.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
        {
            path = path[rootDir.Length..];
            stripped = true;
        }

        // 2. Strip a report-declared source root, absolute or relative.
        foreach (var root in sourceRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path[root.Length..];
                stripped = true;
                break;
            }
        }

        var stillAbsolute = LooksAbsolute(path);
        path = path.TrimStart('/');

        // 3. Without a file list we can only trust root-stripped relative paths.
        if (fileList.Count == 0)
            return (path, Matched: !stillAbsolute);

        // 3a. A relative filename is relative to the report's <source> — that is what
        //     Cobertura means by it. Joining the two is the only way to tell apart two
        //     packages that share a tail (`pipes/src/translate-key.pipe.ts` exists in
        //     both ng-spark and the former ng-spark-auth package), which the suffix match below must, and
        //     does, refuse as ambiguous. Tried before the bare path: a source-relative
        //     `src/x.ts` names `<source>/src/x.ts`, even when a root `src/x.ts` exists.
        if (!stripped && !stillAbsolute)
        {
            var joined = ResolveAgainstSourceRoots(path);
            if (joined is not null)
                return (joined, true);
        }

        var exact = FindExact(path);
        if (exact is not null)
            return (exact, true);

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

    /// <summary>Exact, then case-insensitive exact (Windows-built reports vs POSIX tree).</summary>
    private string? FindExact(string path)
        => fileList.Contains(path)
            ? path
            : fileList.FirstOrDefault(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The tracked file <c>&lt;source&gt;/path</c> names, when exactly one declared source
    /// root yields one. A source root is usable only once it is repo-relative — the
    /// workspace root stripped from an absolute CI path, or relative to begin with; an
    /// absolute root outside the workspace cannot name a tracked file. Two roots naming
    /// two different files is an ambiguity this pass does not settle.
    /// </summary>
    private string? ResolveAgainstSourceRoots(string path)
    {
        string? hit = null;
        foreach (var root in sourceRoots)
        {
            var relativeRoot = root;
            if (rootDir is not null && relativeRoot.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                relativeRoot = relativeRoot[rootDir.Length..];
            if (LooksAbsolute(relativeRoot))
                continue;

            var candidate = FindExact(relativeRoot.TrimStart('/') + path);
            if (candidate is null)
                continue;
            if (hit is not null && !string.Equals(hit, candidate, StringComparison.Ordinal))
                return null;
            hit = candidate;
        }
        return hit;
    }

    private static bool EndsWithPath(string full, string tail)
        => full.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
           && (full.Length == tail.Length || full[full.Length - tail.Length - 1] == '/');

    private static bool LooksAbsolute(string path)
        => path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

    private static string Unify(string path) => path.Replace('\\', '/');
}
