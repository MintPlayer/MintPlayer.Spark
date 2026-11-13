using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// #658: the plan commit moves the root <c>global.json</c>'s <c>sdk.version</c> to the SDK the plan's .NET platform group
/// moves to. <c>dependency-apply.yml</c> installs the SDK <c>global.json</c> names (<c>setup-dotnet</c> with
/// <c>global-json-file</c>) before the action runs, from the dispatched commit, so the job then has the SDK the platform
/// group needs (SpaServices #111: 11.0.100 was never installed). The action puts the old value back before its platform
/// group edits the file, and leaves the move out of the patch when that group is not applied
/// (<c>dependency-action/src/apply/sdkPreset.ts</c> is the same rule).
/// </summary>
public static partial class GlobalJsonPreset
{
    public const string Path = "global.json";

    [GeneratedRegex(@"""sdk""\s*:\s*\{[^{}]*?""version""\s*:\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex SdkVersion();

    [GeneratedRegex(@"^(?<channel>\d+\.\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex Channel();

    /// <summary>
    /// <paramref name="globalJson"/> with only its <c>sdk.version</c> value replaced by the plan's SDK, or null when the
    /// plan commit leaves the file alone: no single .NET platform group with an SDK version, a scoped group
    /// (<c>locations</c>/<c>exclude</c>), no <c>sdk.version</c>, a pin on another channel than the group's, or a target that
    /// is not newer than the pin. Those are the pins the platform group itself would not move.
    /// </summary>
    public static string? Preset(string globalJson, string planJson)
    {
        if (Target(planJson) is not { } target)
            return null;
        var match = SdkVersion().Match(globalJson);
        if (!match.Success)
            return null;
        var value = match.Groups["value"];
        if (Channel().Match(value.Value) is not { Success: true } channel || channel.Groups["channel"].Value != target.FromChannel)
            return null;
        if (!SemVer.TryParse(value.Value, out var current) || !SemVer.TryParse(target.SdkVersion, out var next) || next.CompareTo(current) <= 0)
            return null;
        return string.Concat(globalJson.AsSpan(0, value.Index), target.SdkVersion, globalJson.AsSpan(value.Index + value.Length));
    }

    private static PlanPlatform? Target(string planJson)
    {
        DependencyPlan? plan;
        try
        {
            plan = DependencyContract.Deserialize<DependencyPlan>(planJson);
        }
        catch (JsonException)
        {
            return null;
        }
        var dotnet = plan?.Groups.Select(g => g.Platform).Where(p => p?.Sdk == "dotnet" && p.SdkVersion is { Length: > 0 }).ToList();
        return dotnet is [{ Locations: null or [], Exclude: null or [] } only] ? only : null;
    }
}
