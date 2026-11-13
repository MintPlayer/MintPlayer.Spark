using System.Text.RegularExpressions;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// #658: whether the plan commit may move the root <c>global.json</c> to the planned SDK. A repository runs the dependency
/// action at the SHA its <c>dependency-apply.yml</c> pins, and an action from before #658 refuses a plan commit that changes
/// anything but the plan file, so the whole apply would fail. The bot therefore reads the pinned <c>action.yml</c> and
/// pre-sets only when it declares <c>sdk-preset</c> (the <c># capabilities:</c> line). A repository on an older pin keeps
/// the old behaviour until the updater moves its pin.
/// </summary>
public static partial class DependencySdkPreset
{
    public const string Capability = "sdk-preset";
    public const string ActionYmlPath = "apps/CodeCoverage/dependency-action/action.yml";

    [GeneratedRegex(@"^\s*(?:-\s*)?uses:\s*['""]?MintPlayer/MintPlayer\.Spark/apps/CodeCoverage/dependency-action@(?<ref>[^\s'""#]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Uses();

    [GeneratedRegex(@"^#\s*capabilities:(?<list>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Capabilities();

    /// <summary>The ref the workflow runs the action at, or null when it names none or more than one.</summary>
    public static string? PinnedRef(string? workflow)
    {
        if (workflow is null)
            return null;
        var refs = Uses().Matches(workflow).Select(m => m.Groups["ref"].Value).Distinct(StringComparer.Ordinal).ToList();
        return refs is [var only] ? only : null;
    }

    /// <summary>Whether <paramref name="actionYml"/> declares <see cref="Capability"/>.</summary>
    public static bool Declares(string? actionYml)
        => actionYml is not null && Capabilities().Matches(actionYml)
            .Any(m => m.Groups["list"].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Contains(Capability, StringComparer.Ordinal));
}
