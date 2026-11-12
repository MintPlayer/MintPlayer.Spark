using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.MergeQueue;

namespace CodeCoverage.Onboarding;

/// <summary>
/// #638 D15: the pull request that narrows an older .NET line cut from a commit that also targets a higher major (Tools #55,
/// done by hand). The TFMs above the line are dropped (a project left with none takes <c>net{major}.0</c>), global.json
/// (<c>sdk.version</c> and Arcade's <c>tools.dotnet</c>) and the workflows' <c>setup-dotnet</c> <c>dotnet-version</c> pins take the
/// line's latest SDK (a wildcard such as <c>11.0.x</c> keeps its shape), and framework packages above the line take its latest
/// runtime. The element stays
/// <c>&lt;TargetFrameworks&gt;</c>: switching to the singular changes what MSBuild logic keyed on
/// <c>$(TargetFrameworks)</c> sees, which Tools hit. What cannot be rewritten safely (an MSBuild condition or a
/// <c>#if NETxx_OR_GREATER</c> naming a higher major) is listed for a person.
/// </summary>
/// <param name="Changes">One line per rewritten value.</param>
/// <param name="Conditionals">One line per condition that names a higher major, as <c>`path:line`: `text`</c>.</param>
/// <param name="Notes">What the bot left for a person (an SDK it does not know yet).</param>
public sealed record LineNarrowing(int Major, IReadOnlyList<GitWriterFile> Files, IReadOnlyList<string> Changes, IReadOnlyList<string> Conditionals,
    IReadOnlyList<string> Notes)
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
    private const int MaxListed = 50;

    private static readonly string[] MsBuildExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    private static readonly Regex Tfms = new(@"<(?<name>TargetFrameworks?)>(?<value>[^<]*)</\k<name>>", Options);
    private static readonly Regex TfmMajor = new(@"^\s*net(?:coreapp)?(?<major>\d{1,4})\.", Options | RegexOptions.IgnoreCase);
    private static readonly Regex FrameworkPackage = new(
        @"<(?:PackageReference|PackageVersion)\b[^>]*?\bInclude=""(?<name>Microsoft\.(?:AspNetCore|Extensions|EntityFrameworkCore)\b[^""]*)""[^>]*?\bVersion=""(?<value>[^""]*)""",
        Options | RegexOptions.IgnoreCase);
    private static readonly Regex Sdk = new(@"""sdk""\s*:\s*\{[^{}]*?""version""\s*:\s*""(?<value>[^""]*)""", Options);
    private static readonly Regex ToolsDotnet = new(@"""tools""\s*:\s*\{[^{}]*?""dotnet""\s*:\s*""(?<value>[^""]*)""", Options);
    private static readonly Regex DotnetVersion = new(@"^[ \t]*dotnet-version[ \t]*:[ \t]*(?<quote>['""]?)(?<value>[^'""\s#]+)\k<quote>", Options | RegexOptions.Multiline);
    private static readonly Regex Condition =new(@"\bCondition\s*=\s*""(?<value>[^""]*)""", Options);
    private static readonly Regex ConditionTfm = new(@"\bnet(?:coreapp)?(?<major>\d{1,4})\.\d", Options | RegexOptions.IgnoreCase);
    private static readonly Regex Directive = new(@"^[ \t]*#[ \t]*(?:if|elif)\b[^\r\n]*", Options | RegexOptions.Multiline);
    private static readonly Regex DirectiveTfm = new(@"\bNET(?:COREAPP)?(?<major>\d{1,4})_\d+(?:_OR_GREATER)?\b", Options);
    private static readonly Regex FirstNumber = new(@"\d{1,9}", Options);

    public bool IsEmpty => Files.Count == 0 && Conditionals.Count == 0 && Notes.Count == 0;

    public static bool IsMsBuild(string path) => MsBuildExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsGlobalJson(string path) => path == "global.json" || path.EndsWith("/global.json", StringComparison.Ordinal);

    public static bool IsWorkflow(string path)
        => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
           && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    public static bool IsSource(string path) =>path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an MSBuild file targets a framework above <paramref name="major"/>, so the sources under it are worth
    /// reading for conditionals.</summary>
    public static bool TargetsAbove(int major, string msbuild)
        => Tfms.Matches(msbuild).Any(m => Majors(m.Groups["value"].Value).Any(x => x > major));

    /// <summary>The narrowing of <paramref name="files"/> (path → text: MSBuild files, global.json and C# sources) to
    /// <paramref name="major"/>. <paramref name="latestSdk"/> and <paramref name="latestRuntime"/> are the line's newest
    /// (PlatformLifecycle); null when not collected yet.</summary>
    public static LineNarrowing Plan(int major, IReadOnlyDictionary<string, string> files, string? latestSdk, string? latestRuntime)
    {
        var written = new List<GitWriterFile>();
        var changes = new List<string>();
        var conditionals = new List<string>();
        var notes = new List<string>();
        var prefix = major.ToString(CultureInfo.InvariantCulture) + ".";
        var runtime = latestRuntime is { } r && r.StartsWith(prefix, StringComparison.Ordinal) ? r : $"{major}.0.0";

        foreach (var (path, text) in files.Where(f => IsMsBuild(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var edits = new List<(int Start, int Length, string Value)>();
            foreach (Match match in Tfms.Matches(text))
            {
                var value = match.Groups["value"];
                if (value.Value.Contains("$(", StringComparison.Ordinal) || !Majors(value.Value).Any(m => m > major))
                    continue;
                var kept = value.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(token => Majors(token) is not [var m] || m <= major).ToList();
                var narrowed = kept.Count > 0 ? string.Join(';', kept) : $"net{major}.0";
                edits.Add((value.Index, value.Length, narrowed));
                changes.Add($"`{path}`: {match.Groups["name"].Value} `{value.Value}` → `{narrowed}`");
            }
            foreach (Match match in FrameworkPackage.Matches(text))
            {
                var value = match.Groups["value"];
                if (FirstMajor(value.Value) is not { } m || m <= major)
                    continue;
                edits.Add((value.Index, value.Length, runtime));
                changes.Add($"`{path}`: {match.Groups["name"].Value} `{value.Value}` → `{runtime}`");
            }
            foreach (Match match in Condition.Matches(text))
            {
                if (ConditionTfm.Matches(match.Groups["value"].Value).Any(t => int.Parse(t.Groups["major"].Value, CultureInfo.InvariantCulture) > major))
                    conditionals.Add($"`{path}:{LineOf(text, match.Index)}`: `{match.Value}`");
            }
            if (edits.Count > 0)
                written.Add(new GitWriterFile(path, Rewrite(text, edits)));
        }

        var knownSdk = latestSdk is { } s && s.StartsWith(prefix, StringComparison.Ordinal) ? s : null;
        foreach (var (path, text) in files.Where(f => IsGlobalJson(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            // Arcade's layout pins the SDK twice: `sdk.version` and `tools.dotnet` (SpaServices PR #110).
            var edits = new List<(int Start, int Length, string Value)>();
            foreach (var (pin, label) in new[] { (Sdk, "SDK"), (ToolsDotnet, "tools.dotnet") })
            {
                if (pin.Match(text) is not { Success: true } match || FirstMajor(match.Groups["value"].Value) is not { } m || m <= major)
                    continue;
                var value = match.Groups["value"];
                if (knownSdk is null)
                {
                    notes.Add($"`{path}` pins {label} `{value.Value}`; the latest .NET {major} SDK is not known yet, so set it by hand.");
                    continue;
                }
                edits.Add((value.Index, value.Length, knownSdk));
                changes.Add($"`{path}`: {label} `{value.Value}` → `{knownSdk}`");
            }
            if (edits.Count > 0)
                written.Add(new GitWriterFile(path, Rewrite(text, edits)));
        }

        // The workflows' setup-dotnet pins: global.json alone is not enough when a step installs a pinned SDK, since the
        // narrowed global.json then asks for an SDK the runner does not have (SpaServices PR #110, exit 155).
        foreach (var (path, text) in files.Where(f => IsWorkflow(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var edits = new List<(int Start, int Length, string Value)>();
            foreach (Match match in DotnetVersion.Matches(text))
            {
                var value = match.Groups["value"];
                var line = LineOf(text, match.Index);
                if (value.Value.StartsWith('|') || value.Value.StartsWith('>'))
                {
                    if (BlockAbove(text, match, major))
                        notes.Add($"`{path}:{line}` lists SDKs above .NET {major} under `dotnet-version`; narrow the list by hand.");
                    continue;
                }
                if (FirstMajor(value.Value) is not { } m || m <= major)
                    continue;
                string narrowed;
                if (value.Value.Contains('x', StringComparison.OrdinalIgnoreCase) || value.Value.Contains('*', StringComparison.Ordinal))
                    narrowed = FirstNumber.Replace(value.Value, major.ToString(CultureInfo.InvariantCulture), 1);
                else if (knownSdk is not null)
                    narrowed = knownSdk;
                else
                {
                    notes.Add($"`{path}:{line}` pins dotnet-version `{value.Value}`; the latest .NET {major} SDK is not known yet, so set it by hand.");
                    continue;
                }
                edits.Add((value.Index, value.Length, narrowed));
                changes.Add($"`{path}:{line}`: dotnet-version `{value.Value}` → `{narrowed}`");
            }
            if (edits.Count > 0)
                written.Add(new GitWriterFile(path, Rewrite(text, edits)));
        }

        foreach (var (path, text) in files.Where(f => IsSource(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (Match match in Directive.Matches(text))
            {
                if (DirectiveTfm.Matches(match.Value).Any(t => int.Parse(t.Groups["major"].Value, CultureInfo.InvariantCulture) > major))
                    conditionals.Add($"`{path}:{LineOf(text, match.Index)}`: `{match.Value.Trim()}`");
            }
        }

        return new LineNarrowing(major, written, changes, conditionals, notes);
    }

    /// <summary>The pull request's body: what was narrowed, what a person decides, and the plural-element pitfall.</summary>
    public string Body(string branch)
    {
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture,
            $"`{branch}` was cut from a commit that also targets a higher .NET major. This pull request narrows it to .NET {Major}:\n\n");
        foreach (var line in Changes.Concat(Notes).Take(MaxListed))
            body.Append(CultureInfo.InvariantCulture, $"- {line}\n");
        if (Changes.Count + Notes.Count > MaxListed)
            body.Append(CultureInfo.InvariantCulture, $"- and {Changes.Count + Notes.Count - MaxListed} more\n");
        if (Conditionals.Count > 0)
        {
            body.Append("\nThese conditionals name a higher .NET and are now dead or wrong on this line; resolve them before merging:\n\n");
            foreach (var line in Conditionals.Take(MaxListed))
                body.Append(CultureInfo.InvariantCulture, $"- {line}\n");
            if (Conditionals.Count > MaxListed)
                body.Append(CultureInfo.InvariantCulture, $"- and {Conditionals.Count - MaxListed} more\n");
        }
        body.Append("\nThe projects keep `<TargetFrameworks>` with one value, so MSBuild logic that reads `$(TargetFrameworks)` keeps working. ")
            .Append("Switching them to `<TargetFramework>` changes what that logic sees.\n");
        return body.ToString();
    }

    private static List<int> Majors(string value)
        => [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => TfmMajor.Match(token))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture))];

    private static int? FirstMajor(string value)
        => FirstNumber.Match(value) is { Success: true } m && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;

    /// <summary>Whether a block scalar (<c>dotnet-version: |</c>) lists a version above <paramref name="major"/>: the lines
    /// after the key that are indented deeper than it.</summary>
    private static bool BlockAbove(string text, Match key, int major)
    {
        var lineStart = text.LastIndexOf('\n', key.Index) + 1;
        var indent = 0;
        while (lineStart + indent < text.Length && text[lineStart + indent] is ' ' or '\t') indent++;
        var next = text.IndexOf('\n', key.Index);
        while (next >= 0 && next + 1 < text.Length)
        {
            var end = text.IndexOf('\n', next + 1);
            var line = end < 0 ? text[(next + 1)..] : text[(next + 1)..end];
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.Length > 0)
            {
                if (line.Length - trimmed.Length <= indent)
                    break;
                if (FirstMajor(trimmed) is { } m && m > major)
                    return true;
            }
            next = end;
        }
        return false;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string Rewrite(string text, IEnumerable<(int Start, int Length, string Value)> edits)
    {
        var result = text;
        foreach (var (start, length, value) in edits.OrderByDescending(e => e.Start))
            result = string.Concat(result.AsSpan(0, start), value, result.AsSpan(start + length));
        return result;
    }
}
