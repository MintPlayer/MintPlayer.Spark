using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>Why a regenerate patch is refused (T4). Each is reported as the <c>reason</c> of <c>patch-rejected</c>.</summary>
public static class PatchReasons
{
    public const string TooLarge = "too-large";
    /// <summary>Not UTF-8, a NUL byte, text outside a file section, an unknown header, or a hunk whose line counts do not
    /// match its header.</summary>
    public const string Malformed = "malformed";
    public const string OutsideRegeneratedPaths = "outside-regenerated-paths";
    /// <summary>A path git would quote, with whitespace, <c>..</c>, a <c>.git</c> component, under <c>.github/</c>, or a
    /// <c>.gitmodules</c>/<c>.gitattributes</c> file.</summary>
    public const string UnsafePath = "unsafe-path";
    public const string ModeChange = "mode-change";
    public const string Symlink = "symlink";
    public const string Submodule = "submodule";
    /// <summary>Any mode other than 100644 (an executable file, for example).</summary>
    public const string UnsupportedMode = "unsupported-mode";
    public const string Binary = "binary";
    public const string Copy = "copy";
    /// <summary>git itself refused to apply the validated patch to the prepared tree.</summary>
    public const string ApplyFailed = "apply-failed";
    /// <summary>The applied tree changed a path the patch did not declare (defence in depth; should never happen).</summary>
    public const string UnexpectedChange = "unexpected-change";
    /// <summary>After the apply, a changed path whose <c>autoResolve</c> rule names validators fails one the prepared version
    /// passed (merge-queue PRD §10.4, post-apply validation).</summary>
    public const string InvalidContent = "invalid-content";
    /// <summary>Two regenerate workflows' patches change one path differently (merge-queue PRD O36).</summary>
    public const string Overlap = "overlap";
}

/// <summary>A patch's verdict: the paths it touches, or why it is refused.</summary>
public sealed record PatchVerdict(bool Accepted, string? Reason, IReadOnlyList<string> Paths, string? Detail)
{
    public static PatchVerdict Accept(IReadOnlyList<string> paths) => new(true, null, paths, null);
    public static PatchVerdict Reject(string reason, string? detail, params string[] paths) => new(false, reason, paths, detail);
}

/// <summary>
/// Validates a regenerate patch BEFORE it is applied (docs/coverage_merge_queue_PRD.md §5.7 T4). The regenerate workflow
/// runs the pull request's own code, so its artifact is untrusted: the parser accepts only the strict shape
/// <c>git diff</c> produces for text edits of regular files, and refuses everything else rather than guessing.
/// <list type="bullet">
/// <item>Only <c>diff --git</c> sections; no text before the first or between sections.</item>
/// <item>Every path (old and new) is within <c>regeneratedPaths</c>, is not one git would quote, and is not under
/// <c>.git</c>/<c>.github</c> nor a <c>.gitmodules</c>/<c>.gitattributes</c> file (D12).</item>
/// <item>Modes: only 100644. A mode change, a symlink (120000), a gitlink/submodule (160000), an executable (100755) are
/// refused; so are binary patches and copies. A rename is a delete plus an add, and both paths are checked.</item>
/// <item>Hunks are counted exactly like git counts them: each <c>@@ -a,b +c,d @@</c> is followed by exactly
/// <c>b</c> old and <c>d</c> new lines. Otherwise a line such as <c>--- a/x</c> after a short hunk would be read by
/// <c>git apply</c> as a new, unvalidated file header.</item>
/// <item>The whole patch is capped in bytes and files.</item>
/// </list>
/// After applying, the writer still compares the trees (<see cref="PatchReasons.UnexpectedChange"/>).
/// </summary>
public static partial class PatchValidator
{
    public const int MaxFiles = 500;

    private const string RegularFile = "100644";

    [GeneratedRegex(@"\A@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\Aindex [0-9a-f]{4,64}\.\.[0-9a-f]{4,64}(?: ([0-7]{6}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexLine();

    [GeneratedRegex(@"\A(?:dis)?similarity index \d{1,3}%\z", RegexOptions.CultureInvariant)]
    private static partial Regex SimilarityLine();

    public static PatchVerdict Validate(ReadOnlySpan<byte> patch, IReadOnlyList<PathGlob> regeneratedPaths, long maxBytes)
        => Parse(patch, path => !IsSafePath(path) ? PatchVerdict.Reject(PatchReasons.UnsafePath, null, path)
            : !regeneratedPaths.Any(g => g.Matches(path)) ? PatchVerdict.Reject(PatchReasons.OutsideRegeneratedPaths, null, path)
            : null, maxBytes, files: null);

    /// <summary>
    /// The strict parse behind <see cref="Validate"/>, with the path rule of the caller's profile
    /// (docs/code-coverage/dependency-updates-PRD.md §13.5 P5, R18): <paramref name="pathPolicy"/> answers a refusal for a
    /// path it does not allow, or null. Every shape rule (modes, symlinks, binaries, copies, exact hunk counts, the caps)
    /// holds whatever the policy. With <paramref name="files"/>, the sections are also collected, hunk lines and all.
    /// </summary>
    internal static PatchVerdict Parse(ReadOnlySpan<byte> patch, Func<string, PatchVerdict?> pathPolicy, long maxBytes, List<PatchFile>? files)
    {
        if (patch.Length > maxBytes)
            return PatchVerdict.Reject(PatchReasons.TooLarge, $"The patch is {patch.Length} bytes; the cap is {maxBytes}.");
        if (patch.IndexOf((byte)0) >= 0)
            return PatchVerdict.Reject(PatchReasons.Binary, "The patch contains a NUL byte.");
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(patch);
        }
        catch (DecoderFallbackException)
        {
            return PatchVerdict.Reject(PatchReasons.Malformed, "The patch is not UTF-8.");
        }

        var lines = text.Split('\n');
        // A final newline leaves one empty element; anything else after the last line is part of the content.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var paths = new List<string>();
        var i = 0;
        while (i < count)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1} is outside a file section.");
            if (paths.Count >= 2 * MaxFiles)
                return PatchVerdict.Reject(PatchReasons.TooLarge, $"The patch touches more than {MaxFiles} files.");
            var section = ReadSection(lines, count, ref i, pathPolicy, files);
            if (!section.Accepted)
                return section;
            paths.AddRange(section.Paths);
        }
        return PatchVerdict.Accept(paths.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>One <c>diff --git</c> section: its extended headers, then its hunks.</summary>
    private static PatchVerdict ReadSection(string[] lines, int count, ref int i, Func<string, PatchVerdict?> pathPolicy, List<PatchFile>? files)
    {
        var header = lines[i]["diff --git ".Length..];
        var start = i + 1;
        i++;
        string? oldPath = null, newPath = null, renameFrom = null, renameTo = null;
        var isNew = false;
        var isDeleted = false;
        var sawMinus = false;
        var sawPlus = false;

        // Extended headers until the first hunk or the next section.
        while (i < count && !lines[i].StartsWith("@@ ", StringComparison.Ordinal) && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
        {
            var line = lines[i];
            if (line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal))
                return PatchVerdict.Reject(PatchReasons.ModeChange, $"Line {i + 1}: {line}");
            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                if (Mode(line["new file mode ".Length..], i) is { } bad)
                    return bad;
                isNew = true;
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                if (Mode(line["deleted file mode ".Length..], i) is { } bad)
                    return bad;
                isDeleted = true;
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
                return PatchVerdict.Reject(PatchReasons.Copy, $"Line {i + 1}: a copy.");
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                renameFrom = line["rename from ".Length..];
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                renameTo = line["rename to ".Length..];
            else if (SimilarityLine().IsMatch(line))
            {
            }
            else if (IndexLine().Match(line) is { Success: true } index)
            {
                if (index.Groups[1].Success && Mode(index.Groups[1].Value, i) is { } bad)
                    return bad;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
                return PatchVerdict.Reject(PatchReasons.Binary, $"Line {i + 1}: a binary patch.");
            else if (line.StartsWith("--- ", StringComparison.Ordinal) && !sawMinus)
            {
                sawMinus = true;
                var value = line[4..];
                if (value != "/dev/null")
                {
                    if (!value.StartsWith("a/", StringComparison.Ordinal))
                        return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: an old path without a/.");
                    oldPath = value[2..];
                }
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal) && sawMinus && !sawPlus)
            {
                sawPlus = true;
                var value = line[4..];
                if (value != "/dev/null")
                {
                    if (!value.StartsWith("b/", StringComparison.Ordinal))
                        return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: a new path without b/.");
                    newPath = value[2..];
                }
            }
            else
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1} is not a header git diff writes.");
            i++;
        }

        // The section's paths: the header's a/P b/P when both are the same, else the rename lines; ---/+++ must agree.
        string? headerPath = null;
        if (header.StartsWith("a/", StringComparison.Ordinal) && (header.Length - 5) % 2 == 0 && header.Length >= 7)
        {
            var length = (header.Length - 5) / 2;
            var candidate = header.Substring(2, length);
            if (header == $"a/{candidate} b/{candidate}")
                headerPath = candidate;
        }
        var from = renameFrom ?? headerPath;
        var to = renameTo ?? headerPath;
        if ((renameFrom is null) != (renameTo is null) || from is null || to is null)
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: the section's paths cannot be read unambiguously.");
        if (renameFrom is not null && (header != $"a/{renameFrom} b/{renameTo}"))
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: the rename does not match the section header.");
        if (isNew && isDeleted)
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: a file both added and deleted.");
        if (sawMinus != sawPlus)
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: --- without +++.");
        if (sawMinus && (oldPath != (isNew ? null : from) || newPath != (isDeleted ? null : to)))
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: ---/+++ do not match the section header.");

        var touched = renameFrom is null ? new[] { from } : [from, to];
        foreach (var path in touched)
        {
            if (pathPolicy(path) is { } refused)
                return refused;
        }

        // Hunks, counted exactly.
        var hunks = 0;
        var collected = files is null ? null : new List<PatchHunk>();
        while (i < count && lines[i].StartsWith("@@ ", StringComparison.Ordinal))
        {
            var hunkLines = collected is null ? null : new List<PatchLine>();
            var match = HunkHeader().Match(lines[i]);
            if (!match.Success)
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: a hunk header that does not parse.");
            if (!sawMinus)
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: a hunk without ---/+++.");
            var oldLines = Count(match.Groups[2]);
            var newLines = Count(match.Groups[4]);
            if (oldLines is null || newLines is null)
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: a hunk count out of range.");
            // Only a collecting parse needs the starts (Validate's verdicts stay exactly what they were).
            var oldStart = 0;
            var newStart = 0;
            if (files is not null && (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out oldStart)
                || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out newStart)))
                return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1}: a hunk start out of range.");
            i++;
            int remainingOld = oldLines.Value, remainingNew = newLines.Value;
            while (remainingOld > 0 || remainingNew > 0)
            {
                if (i >= count)
                    return PatchVerdict.Reject(PatchReasons.Malformed, "The patch ends inside a hunk.");
                var line = lines[i];
                var kind = line.Length == 0 ? ' ' : line[0];
                switch (kind)
                {
                    case ' ' when remainingOld > 0 && remainingNew > 0:
                        remainingOld--;
                        remainingNew--;
                        break;
                    case '-' when remainingOld > 0:
                        remainingOld--;
                        break;
                    case '+' when remainingNew > 0:
                        remainingNew--;
                        break;
                    case '\\':
                        if (hunkLines is { Count: > 0 })
                            hunkLines[^1] = hunkLines[^1] with { NoNewline = true };
                        break;
                    default:
                        return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1} does not fit its hunk's counts.");
                }
                if (kind != '\\')
                    hunkLines?.Add(new PatchLine(kind, line.Length == 0 ? string.Empty : line[1..], false));
                i++;
            }
            // "\ No newline at end of file" may follow the hunk's last line.
            while (i < count && lines[i].StartsWith('\\'))
            {
                if (hunkLines is { Count: > 0 })
                    hunkLines[^1] = hunkLines[^1] with { NoNewline = true };
                i++;
            }
            collected?.Add(new PatchHunk(oldStart, oldLines.Value, newStart, newLines.Value, hunkLines!));
            hunks++;
        }
        if (i < count && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {i + 1} is outside a hunk.");
        if (hunks == 0 && !isNew && !isDeleted && renameFrom is null)
            return PatchVerdict.Reject(PatchReasons.Malformed, $"Line {start}: a section with no change git diff would write.");
        files?.Add(new PatchFile(from, to, isNew, isDeleted, renameFrom is not null, collected!));
        return PatchVerdict.Accept(touched);
    }

    private static PatchVerdict? Mode(string mode, int line) => mode switch
    {
        RegularFile => null,
        "120000" => PatchVerdict.Reject(PatchReasons.Symlink, $"Line {line + 1}: a symlink."),
        "160000" => PatchVerdict.Reject(PatchReasons.Submodule, $"Line {line + 1}: a submodule."),
        _ => PatchVerdict.Reject(PatchReasons.UnsupportedMode, $"Line {line + 1}: mode {mode}."),
    };

    private static int? Count(Group group)
    {
        if (!group.Success)
            return 1;
        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 10_000_000 ? value : null;
    }

    /// <summary>A path the writer is willing to write: printable ASCII without spaces, quotes or backslashes (git would
    /// quote it otherwise), no empty, <c>.</c> or <c>..</c> component, nothing under <c>.git</c> or <c>.github</c>, and
    /// not a <c>.gitmodules</c> or <c>.gitattributes</c> file (case-insensitive, as on the file systems that fold case).</summary>
    internal static bool IsSafePath(string path) => IsWritablePath(path) && !IsUnderGitHub(path);

    /// <summary><see cref="IsSafePath"/> without the <c>.github/</c> rule, for the dependency profile's workflow carve-out
    /// (docs/code-coverage/dependency-updates-PRD.md R15), which decides on <c>.github/</c> paths itself.</summary>
    internal static bool IsWritablePath(string path)
    {
        if (path.Length is 0 or > 4096 || path.Any(c => c <= ' ' || c > '~' || c is '"' or '\\'))
            return false;
        var components = path.Split('/');
        foreach (var component in components)
        {
            if (component is "" or "." or ".." || component.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        var name = components[^1];
        return !name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) && !name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path's first component is <c>.github</c>, in any case.</summary>
    internal static bool IsUnderGitHub(string path)
        => path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || path.Equals(".github", StringComparison.OrdinalIgnoreCase);
}
