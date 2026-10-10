using System.Globalization;
using System.Text;

namespace SeoTester.Crawling;

/// <summary>What the fetch of <c>/robots.txt</c> gave (RFC 9309 §2.3.1).</summary>
public enum RobotsAvailability
{
    /// <summary>2xx: the rules apply.</summary>
    Available,

    /// <summary>4xx: "unavailable", the crawler may access any resource.</summary>
    Unavailable,

    /// <summary>5xx or a network failure: "unreachable", the crawler must assume a complete disallow.</summary>
    Unreachable,
}

/// <summary>
/// robots.txt per RFC 9309, for one product token (<see cref="SeoTesterBot.RobotsToken"/>, the only opt-out, D19):
/// <list type="bullet">
/// <item>the groups whose <c>user-agent</c> names the token (case-insensitive) apply, merged; when none does, the
/// <c>*</c> groups do; when neither exists, everything is allowed;</item>
/// <item>the longest matching rule wins (its length in octets), and <c>allow</c> wins a tie; <c>*</c> matches any
/// sequence and a final <c>$</c> anchors the end;</item>
/// <item>at most <see cref="MaxBytes"/> are parsed (the RFC's minimum is 500 KiB);</item>
/// <item><c>sitemap</c> lines are collected from anywhere; a <c>crawl-delay</c> in the applying group is read, though
/// the RFC does not define it, because honouring it is polite.</item>
/// </list>
/// Matching is a hand-written wildcard walk, not a regex: the input is untrusted and must not be able to make it slow.
/// </summary>
public sealed class RobotsTxt
{
    /// <summary>Bytes of robots.txt that are parsed.</summary>
    public const int MaxBytes = 512 * 1024;

    private readonly List<RobotsRule> rules;

    private RobotsTxt(RobotsAvailability availability, List<RobotsRule> rules, IReadOnlyList<string> sitemaps, double? crawlDelaySeconds, bool hasOwnGroup,
        bool disallowsAllForEveryone, bool disallowsAllForGooglebot, int lines, int unknownLines)
    {
        Availability = availability;
        this.rules = rules;
        Sitemaps = sitemaps;
        CrawlDelaySeconds = crawlDelaySeconds;
        HasOwnGroup = hasOwnGroup;
        DisallowsAllForEveryone = disallowsAllForEveryone;
        DisallowsAllForGooglebot = disallowsAllForGooglebot;
        Lines = lines;
        UnknownLines = unknownLines;
    }

    public RobotsAvailability Availability { get; }

    /// <summary>The rules that apply to the token, in file order.</summary>
    public IReadOnlyList<RobotsRule> Rules => rules;

    /// <summary>Every <c>sitemap</c> URL, as written.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>The applying group's <c>crawl-delay</c>, when it has a valid one.</summary>
    public double? CrawlDelaySeconds { get; }

    /// <summary>A group names the token itself (not only <c>*</c>).</summary>
    public bool HasOwnGroup { get; }

    /// <summary>The <c>*</c> group disallows <c>/</c>.</summary>
    public bool DisallowsAllForEveryone { get; }

    /// <summary>Googlebot (its own group, else <c>*</c>) may not fetch <c>/</c>.</summary>
    public bool DisallowsAllForGooglebot { get; }

    /// <summary>Non-empty, non-comment lines read.</summary>
    public int Lines { get; }

    /// <summary>Lines that are neither a known field nor a comment (typos such as <c>Dissallow</c>).</summary>
    public int UnknownLines { get; }

    /// <summary>The token may fetch nothing: the site opted out (its own group) or disallows everyone, or robots.txt is unreachable.</summary>
    public bool DisallowsAll => !IsAllowed("/");

    /// <summary>The site opted out of SeoTester by name (D19).</summary>
    public bool OptedOut => HasOwnGroup && DisallowsAll;

    /// <summary>Everything allowed: robots.txt missing (4xx) or empty.</summary>
    public static RobotsTxt AllowAll { get; } = new(RobotsAvailability.Unavailable, [], [], null, false, false, false, 0, 0);

    /// <summary>Everything disallowed: robots.txt unreachable (5xx, network).</summary>
    public static RobotsTxt DisallowAll { get; } = new(RobotsAvailability.Unreachable, [new RobotsRule(false, "/")], [], null, false, false, false, 0, 0);

    /// <summary>The applying rules again, as the crawl frontier keeps them between batches.</summary>
    public static RobotsTxt FromRules(IEnumerable<RobotsRule> applying, double? crawlDelaySeconds)
        => new(RobotsAvailability.Available, applying.ToList(), [], crawlDelaySeconds, false, false, false, 0, 0);

    /// <summary>Whether the token may fetch <paramref name="pathAndQuery"/> (starting with <c>/</c>).</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        if (pathAndQuery == "/robots.txt")
            return true;
        var path = NormalizeEncoding(pathAndQuery);
        RobotsRule? best = null;
        foreach (var rule in rules)
        {
            if (!Matches(rule.Pattern, path))
                continue;
            if (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
                best = rule;
        }
        return best?.Allow ?? true;
    }

    public bool IsAllowed(Uri uri) => IsAllowed(string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery);

    /// <summary>Parses robots.txt for <paramref name="token"/>.</summary>
    public static RobotsTxt Parse(string? content, string token)
    {
        content ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
            content = TruncateToBytes(content, MaxBytes);

        var groups = new List<(List<string> Agents, List<RobotsRule> Rules, double? Delay)>();
        var sitemaps = new List<string>();
        List<string>? agents = null;
        List<RobotsRule>? groupRules = null;
        double? delay = null;
        var lastWasAgent = false;
        int lines = 0, unknown = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            line = line.Trim();
            if (line.Length == 0)
                continue;
            lines++;
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                unknown++;
                continue;
            }
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    if (!lastWasAgent)
                    {
                        agents = [];
                        groupRules = [];
                        delay = null;
                        groups.Add((agents, groupRules, null));
                    }
                    agents!.Add(value);
                    lastWasAgent = true;
                    break;
                case "allow":
                case "disallow":
                    lastWasAgent = false;
                    if (groupRules is null || value.Length == 0)
                        break;
                    groupRules.Add(new RobotsRule(key == "allow", NormalizeEncoding(value)));
                    break;
                case "crawl-delay":
                    lastWasAgent = false;
                    if (groupRules is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    {
                        delay = seconds;
                        groups[^1] = (groups[^1].Agents, groups[^1].Rules, delay);
                    }
                    break;
                case "sitemap":
                    if (value.Length > 0 && sitemaps.Count < 50)
                        sitemaps.Add(value);
                    break;
                default:
                    lastWasAgent = false;
                    unknown++;
                    break;
            }
        }

        var (own, ownRules, ownDelay) = Select(groups, token);
        var star = Select(groups, "*");
        var googlebot = Select(groups, "Googlebot");
        var applying = own ? ownRules : star.Rules;
        var parsed = new RobotsTxt(RobotsAvailability.Available, applying, sitemaps, own ? ownDelay : star.Delay, own,
            DisallowsRoot(star.Rules), DisallowsRoot(googlebot.Found ? googlebot.Rules : star.Rules), lines, unknown);
        return parsed;
    }

    private static (bool Found, List<RobotsRule> Rules, double? Delay) Select(List<(List<string> Agents, List<RobotsRule> Rules, double? Delay)> groups, string token)
    {
        var found = false;
        var merged = new List<RobotsRule>();
        double? delay = null;
        foreach (var group in groups)
        {
            if (!group.Agents.Any(a => AgentMatches(a, token)))
                continue;
            found = true;
            merged.AddRange(group.Rules);
            delay ??= group.Delay;
        }
        return (found, merged, delay);
    }

    /// <summary>The <c>user-agent</c> value's product token (up to the first character that is no letter, <c>-</c> or <c>_</c>) equals the token.</summary>
    private static bool AgentMatches(string agent, string token)
    {
        if (token == "*")
            return agent.Trim() == "*";
        var end = 0;
        while (end < agent.Length && (char.IsAsciiLetter(agent[end]) || agent[end] is '-' or '_'))
            end++;
        return end > 0 && string.Equals(agent[..end], token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DisallowsRoot(List<RobotsRule> groupRules)
    {
        var probe = new RobotsTxt(RobotsAvailability.Available, groupRules, [], null, false, false, false, 0, 0);
        return !probe.IsAllowed("/");
    }

    /// <summary>
    /// Wildcard match from the start of <paramref name="path"/>: <c>*</c> = any sequence, a final <c>$</c> = end.
    /// Iterative with one backtrack point, so linear-ish and never exponential.
    /// </summary>
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored)
            pattern = pattern[..^1];
        int p = 0, s = 0, starP = -1, starS = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starS = s;
            }
            else if (p < pattern.Length && pattern[p] == path[s])
            {
                p++;
                s++;
            }
            else if (p == pattern.Length && !anchored)
                return true;
            else if (starP >= 0)
            {
                p = starP + 1;
                s = ++starS;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>Upper-cases percent-encodings so <c>%2f</c> and <c>%2F</c> compare equal (RFC 9309 §2.2.2).</summary>
    private static string NormalizeEncoding(string value)
    {
        if (!value.Contains('%'))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                builder.Append('%').Append(char.ToUpperInvariant(value[i + 1])).Append(char.ToUpperInvariant(value[i + 2]));
                i += 2;
            }
            else
                builder.Append(value[i]);
        }
        return builder.ToString();
    }

    private static string TruncateToBytes(string content, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return Encoding.UTF8.GetString(bytes, 0, maxBytes);
    }
}

/// <summary>One <c>allow</c> or <c>disallow</c> line.</summary>
public sealed record RobotsRule(bool Allow, string Pattern);
