using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>
/// The on-demand format profile (docs/coverage_file_formatter_PRD.md D15): a FormatRepository or LintRepository run only
/// rewrites files, so its patch may edit any file <see cref="PatchValidator.IsWritablePath"/> allows, <c>.github/**</c>
/// included (D7), but never one the repository's <c>exclude</c> globs name (D11), and it never adds, deletes or renames a
/// file. <see cref="PatchValidator"/>'s shape rules hold as for every profile: only mode 100644, no binary, no copy.
/// </summary>
public static class FormatPatchValidator
{
    /// <summary>A formatter rewrote a file it may not add, delete or rename.</summary>
    public const string NotAnEdit = "not-an-edit";

    /// <summary>The patch's paths, and its sections when <paramref name="files"/> is given; or why it is refused.</summary>
    public static PatchVerdict Validate(ReadOnlySpan<byte> patch, IReadOnlyList<string> exclude, long maxBytes, List<PatchFile>? files = null)
    {
        var globs = exclude.Select(Glob).OfType<PathGlob>().ToList();
        var sections = files ?? [];
        var verdict = PatchValidator.Parse(patch, path => !PatchValidator.IsWritablePath(path) ? PatchVerdict.Reject(PatchReasons.UnsafePath, null, path)
            : globs.Any(g => g.Matches(path)) ? PatchVerdict.Reject(PatchReasons.OutsideRegeneratedPaths, "The repository's formatting workflow excludes it.", path)
            : null, maxBytes, sections);
        if (!verdict.Accepted)
            return verdict;
        if (sections.FirstOrDefault(f => f.IsNew || f.IsDeleted || f.IsRename) is { } file)
            return PatchVerdict.Reject(NotAnEdit, file.IsNew ? "A formatter does not add files." : file.IsDeleted ? "A formatter does not delete files." : "A formatter does not rename files.",
                file.IsDeleted ? file.OldPath : file.NewPath);
        return verdict;
    }

    /// <summary>
    /// An <c>exclude</c> entry read as the format action reads it (<c>globToRegExp</c> in <c>format.mjs</c>): a leading
    /// <c>./</c> is dropped, a trailing <c>/</c> means everything below, and a glob without a <c>/</c> matches at any depth,
    /// as in <c>.gitignore</c>. Null for a blank entry.
    /// </summary>
    public static PathGlob? Glob(string entry)
    {
        var glob = entry.Trim();
        if (glob.StartsWith("./", StringComparison.Ordinal))
            glob = glob[2..];
        if (glob.Length == 0)
            return null;
        if (!glob.Contains('/'))
            glob = "**/" + glob;
        else if (glob.EndsWith('/'))
            glob += "**";
        return new PathGlob(glob);
    }

    /// <summary>Whether the patch edits a workflow (<c>.github/**</c>), which needs the token's <c>workflows: write</c>.</summary>
    public static bool TouchesGitHub(PatchVerdict verdict) => verdict.Paths.Any(PatchValidator.IsUnderGitHub);
}
