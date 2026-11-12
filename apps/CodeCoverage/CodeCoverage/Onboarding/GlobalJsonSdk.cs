using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.MergeQueue;

namespace CodeCoverage.Onboarding;

/// <summary>
/// #649 D11-D15, the opt-in "SDK from global.json" conversion: the workflows' <c>actions/setup-dotnet</c> steps read the SDK
/// from the root <c>global.json</c> (<c>global-json-file: global.json</c>) instead of pinning a <c>dotnet-version</c>, so one
/// file says which SDK a branch builds with, and the SDK updater and a line's narrowing only rewrite that file.
/// <list type="bullet">
/// <item>D13: only where the root global.json has <c>sdk.version</c>; the bot never creates one.</item>
/// <item>D14: every scalar pin converts ("Always use the same major", owner 2026-11-12).</item>
/// <item>D15: a block list keeps its entries below global.json's major and loses the one on it, which
/// <c>global-json-file</c> installs instead (setup-dotnet installs both inputs, S11).</item>
/// <item>S11: setup-dotnet fails when the file is missing, so only a step after an <c>actions/checkout</c> in the same job
/// converts; one before it is listed for a person.</item>
/// </list>
/// Line-based like <see cref="LineNarrowing"/>, which rewrites the same pins: the workflow keeps its comments and layout.
/// </summary>
/// <param name="Files">The rewritten workflows.</param>
/// <param name="Changes">One line per converted step.</param>
/// <param name="Notes">What the bot left for a person.</param>
public sealed record GlobalJsonSdk(IReadOnlyList<GitWriterFile> Files, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes)
{
    public const string GlobalJsonPath = "global.json";
    public const string Key = "global-json-file: global.json";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
    private static readonly Regex Sdk = new(@"""sdk""\s*:\s*\{[^{}]*?""version""\s*:\s*""(?<value>[^""]*)""", Options);
    private static readonly Regex DotnetVersion = new(@"^(?<indent>[ \t]*)(?<dash>-[ \t]+)?dotnet-version[ \t]*:[ \t]*(?<quote>['""]?)(?<value>[^'""\s#]*)\k<quote>", Options);
    private static readonly Regex Checkout = new(@"^[ \t]*(-[ \t]+)?uses[ \t]*:[ \t]*['""]?actions/checkout@", Options);
    private static readonly Regex GlobalJsonFile = new(@"^[ \t]*(-[ \t]+)?global-json-file[ \t]*:", Options);
    private static readonly Regex FirstNumber = new(@"\d{1,9}", Options);

    public bool IsEmpty => Files.Count == 0 && Notes.Count == 0;

    /// <summary>The SDK major of a global.json's <c>sdk.version</c>; null without one.</summary>
    public static int? SdkMajor(string? globalJson)
        => globalJson is not null && Sdk.Match(globalJson) is { Success: true } match ? FirstMajor(match.Groups["value"].Value) : null;

    /// <summary>
    /// The conversion of <paramref name="files"/> (path → text; the root global.json and the workflows, other paths are
    /// ignored). Planning again on the result changes nothing.
    /// </summary>
    public static GlobalJsonSdk Plan(IReadOnlyDictionary<string, string> files)
    {
        var written = new List<GitWriterFile>();
        var changes = new List<string>();
        var notes = new List<string>();
        var workflows = files.Where(f => LinePaths.IsWorkflow(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        if (!workflows.Any(w => w.Value.Split('\n').Any(l => DotnetVersion.IsMatch(l))))
            return new GlobalJsonSdk(written, changes, notes);

        if (SdkMajor(files.GetValueOrDefault(GlobalJsonPath)) is not { } major)
        {
            notes.Add("There is no `global.json` with an `sdk.version` at the root, so the workflows keep their `dotnet-version` pins. Add one to read the SDK from it.");
            return new GlobalJsonSdk(written, changes, notes);
        }

        foreach (var (path, text) in workflows)
        {
            var lines = text.Split('\n').ToList();
            var converted = false;
            var fileChanges = new List<string>();
            var jobs = lines.FindIndex(l => l.TrimEnd('\r').TrimEnd() == "jobs:");
            var jobIndent = jobs < 0 ? -1 : lines.Skip(jobs + 1).Where(IsContent).Select(Indent).FirstOrDefault(-1);
            // Bottom-up, so an edit never moves a line still to be read.
            for (var index = lines.Count - 1; index > jobs && jobs >= 0; index--)
            {
                if (DotnetVersion.Match(lines[index].TrimEnd('\r')) is not { Success: true } match)
                    continue;
                var lineNumber = index + 1;
                var keyIndent = match.Groups["indent"].Length + match.Groups["dash"].Length;
                var (stepStart, stepEnd) = Step(lines, index, keyIndent);
                if (Enumerable.Range(stepStart, stepEnd - stepStart).Any(i => GlobalJsonFile.IsMatch(lines[i])))
                    continue;   // converted already
                var jobStart = Enumerable.Range(jobs + 1, index - jobs - 1).LastOrDefault(i => IsContent(lines[i]) && Indent(lines[i]) == jobIndent, -1);
                if (jobStart < 0 || !Enumerable.Range(jobStart, stepStart - jobStart).Any(i => Checkout.IsMatch(lines[i])))
                {
                    notes.Add($"`{path}:{lineNumber}` installs the SDK before the job checks out the repository, so it cannot read `global.json`; it keeps its pin.");
                    continue;
                }

                var value = match.Groups["value"].Value;
                var prefix = lines[index][..(match.Groups["indent"].Length + match.Groups["dash"].Length)];
                if (value is "|" or "|-" or ">" or ">-" or "")
                {
                    var blockEnd = index + 1;
                    while (blockEnd < lines.Count && (!IsContent(lines[blockEnd]) || Indent(lines[blockEnd]) > keyIndent))
                        blockEnd++;
                    while (blockEnd > index + 1 && !IsContent(lines[blockEnd - 1]))
                        blockEnd--;
                    var entries = Enumerable.Range(index + 1, blockEnd - index - 1).Where(i => IsContent(lines[i])).ToList();
                    if (entries.Count == 0)
                        continue;   // `dotnet-version:` with nothing under it is not a pin
                    var onMajor = entries.Where(i => FirstMajor(lines[i].Trim()) == major).ToList();
                    var kept = entries.Except(onMajor).Select(i => $"`{lines[i].Trim()}`").ToList();
                    if (kept.Count == 0)
                    {
                        lines.RemoveRange(index + 1, blockEnd - index - 1);
                        lines[index] = prefix + Key + CarriageReturn(lines[index]);
                        fileChanges.Add($"`{path}:{lineNumber}`: dotnet-version list → `{Key}`");
                    }
                    else
                    {
                        lines.Insert(blockEnd, new string(' ', keyIndent) + Key + CarriageReturn(lines[index]));
                        foreach (var remove in onMajor.OrderDescending())
                            lines.RemoveAt(remove);
                        fileChanges.Add(onMajor.Count == 0
                            ? $"`{path}:{lineNumber}`: `{Key}` added beside the dotnet-version list"
                            : $"`{path}:{lineNumber}`: dotnet-version list loses .NET {major} for `{Key}`, keeps {string.Join(", ", kept)}");
                    }
                }
                else if (value.StartsWith('[') || value.StartsWith('$'))
                {
                    notes.Add($"`{path}:{lineNumber}` sets dotnet-version `{value}…` in a form the bot does not rewrite; switch it to `{Key}` by hand.");
                    continue;
                }
                else
                {
                    var end = match.Groups["value"].Index + match.Groups["value"].Length + match.Groups["quote"].Length;
                    lines[index] = prefix + Key + lines[index][end..];
                    fileChanges.Add($"`{path}:{lineNumber}`: dotnet-version `{value}` → `{Key}`");
                }
                converted = true;
            }
            if (converted)
                written.Add(new GitWriterFile(path, string.Join('\n', lines)));
            fileChanges.Reverse();
            changes.AddRange(fileChanges);
        }
        return new GlobalJsonSdk(written, changes, notes);
    }

    /// <summary>The step that holds line <paramref name="index"/>: from its <c>- </c> line to the next line no deeper than it.</summary>
    private static (int Start, int End) Step(List<string> lines, int index, int keyIndent)
    {
        var start = index;
        while (start > 0 && !(IsContent(lines[start]) && lines[start].TrimStart().StartsWith('-') && Indent(lines[start]) < keyIndent))
            start--;
        var dash = Indent(lines[start]);
        var end = index + 1;
        while (end < lines.Count && (!IsContent(lines[end]) || Indent(lines[end]) > dash))
            end++;
        return (start, end);
    }

    private static bool IsContent(string line) => line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart(' ', '\t').Length;

    private static string CarriageReturn(string line) => line.EndsWith('\r') ? "\r" : "";

    private static int? FirstMajor(string value)
        => FirstNumber.Match(value) is { Success: true } m && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;
}
