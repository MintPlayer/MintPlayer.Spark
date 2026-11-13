using System.Text.RegularExpressions;

namespace CodeCoverage.Onboarding;

/// <summary>
/// Owner, 2026-11-13 (supersedes D-L14's commit pin): the workflows call the bot's actions at their moving tag
/// (<c>dependency-update-v1</c>, <c>versioning-v1</c>), which each release of the action moves. A commit pin only moved when
/// the rolling pull request that bumped it merged, so a fix to the action could not reach the run that needed it
/// (Dotnet.Tools #208, run 43483039627: master's pin 5c99269 ran while #208 carried 7c6e7f33, and every NuGet group failed
/// with NETSDK1045). Re-onboarding moves a repository's existing pin back to the tag.
/// </summary>
public static class ActionTagReference
{
    /// <summary>
    /// <paramref name="text"/> with every <c>{actionPath}@&lt;40-hex commit&gt;</c> reference, bare or followed by
    /// <c># {tag}</c>, moved to <c>{actionPath}@{tag}</c>. A reference with another comment (another tag) is left alone.
    /// </summary>
    public static string AtTag(string text, string actionPath, string tag)
        => Regex.Replace(text,
            $@"(?<![\w./-]){Regex.Escape(actionPath)}@[0-9a-f]{{40}}(?:[ \t]+#[ \t]*{Regex.Escape(tag)})?[ \t]*(?=\r?$)",
            $"{actionPath}@{tag}", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
