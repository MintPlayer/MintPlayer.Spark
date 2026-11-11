using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.Entities;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>A release a preview feed names: its channel, version and date, and the name a toolchain file pins it by.</summary>
public sealed record PlatformPreview(string Channel, string Version, DateTime? ReleasedOn, string? Name = null);

/// <summary>
/// Reads the platform lifecycle sources (#568 S10, PRD §8.1; #606 D6, S6) into <see cref="PlatformRelease"/> rows. Pure:
/// the collector fetches, these only parse, against the real responses committed as test fixtures.
/// </summary>
public static partial class PlatformLifecycleSources
{
    public const string Dotnet = "dotnet";

    /// <summary>
    /// The .NET release index: one row per channel. <c>go-live</c> (the release candidates) is still a preview. The index
    /// names no GA date, so <see cref="PlatformRelease.ReleasedOn"/> stays null; a channel's own <c>releases.json</c> has it
    /// but weighs 1.15 MB for 10.0 (2026-11-09), so the latest release's date stands in.
    /// </summary>
    public static IReadOnlyList<PlatformRelease> ReadDotnet(string json, string source, DateTime collectedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<PlatformRelease>();
        foreach (var release in document.RootElement.GetProperty("releases-index").EnumerateArray())
        {
            if (String(release, "channel-version") is not { } name || !IsChannel(name) || rows.Any(r => r.Channel == name)) continue;
            rows.Add(new PlatformRelease
            {
                Id = PlatformRelease.IdFor(Dotnet, name),
                Platform = Dotnet,
                Channel = name,
                Major = MajorOf(name) ?? 0,
                Name = name,
                Phase = String(release, "support-phase") switch
                {
                    "preview" or "go-live" => "preview",
                    "active" => "active",
                    "maintenance" => "maintenance",
                    _ => "eol",
                },
                IsLts = String(release, "release-type") == "lts",
                SupportEndsOn = Date(release, "eol-date"),
                LatestVersion = String(release, "latest-release"),
                LatestReleasedOn = Date(release, "latest-release-date"),
                LatestSdk = String(release, "latest-sdk"),
                Source = source,
                CollectedAtUtc = collectedAtUtc,
            });
        }
        return [.. rows.OrderByDescending(r => r.Channel, ChannelComparer.Instance)];
    }

    /// <summary>
    /// An endoflife.date v1 product, one row per release (a channel). The phase comes from its booleans: <c>isEol</c> ends
    /// support, <c>isEoas</c> ends active support. <c>isMaintained</c> is not used: it stays true while only commercial
    /// support is left (Angular 9).
    /// </summary>
    public static IReadOnlyList<PlatformRelease> ReadEndOfLife(string platform, string json, string source, DateTime collectedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<PlatformRelease>();
        foreach (var release in document.RootElement.GetProperty("result").GetProperty("releases").EnumerateArray())
        {
            if (String(release, "name") is not { } name || !IsChannel(name) || MajorOf(name) is not { } major) continue;
            if (rows.Any(r => r.Channel == name)) continue;
            var latest = release.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.Object ? l : (JsonElement?)null;
            rows.Add(new PlatformRelease
            {
                Id = PlatformRelease.IdFor(platform, name),
                Platform = platform,
                Channel = name,
                Major = major,
                Name = name,
                Phase = Bool(release, "isEol") ? "eol" : Bool(release, "isEoas") ? "maintenance" : "active",
                IsLts = Bool(release, "isLts"),
                ReleasedOn = Date(release, "releaseDate"),
                ActiveSupportEndsOn = Date(release, "eoasFrom"),
                SupportEndsOn = Date(release, "eolFrom"),
                LatestVersion = latest is { } a ? String(a, "name") : null,
                LatestReleasedOn = latest is { } b ? Date(b, "date") : null,
                Source = source,
                CollectedAtUtc = collectedAtUtc,
            });
        }
        return [.. rows.OrderByDescending(r => r.Channel, ChannelComparer.Instance)];
    }

    /// <summary>
    /// A framework's preview major from its npm dist-tags (endoflife.date lists none): <c>next</c>, when its major is
    /// above <c>latest</c>'s. A <c>next</c> left behind on an older or the same major (NestJS, React canaries) is none.
    /// </summary>
    public static (int Major, string Version)? ReadPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (String(document.RootElement, "latest") is not { } latest || String(document.RootElement, "next") is not { } next) return null;
        return MajorOf(next) is { } nextMajor && MajorOf(latest) is { } latestMajor && nextMajor > latestMajor ? (nextMajor, next) : null;
    }

    /// <summary>
    /// The preview releases of an SDK (#606 S6), newest channel first, from the feed the collector reads for it:
    /// node's release-candidate index, python.org's pre-releases, php.net's release candidates, ruby-lang.org's release
    /// list, and Rust's beta channel manifest. Null for a platform without one.
    /// </summary>
    public static IReadOnlyList<PlatformPreview>? ReadPreviews(string platform, string text) => platform switch
    {
        "node" => ReadNodeCandidates(text),
        "python" => ReadPythonPrereleases(text),
        "php" => ReadPhpCandidates(text),
        "ruby" => ReadRubyPreviews(text),
        "rust" => ReadRustBeta(text),
        _ => null,
    };

    /// <summary><c>https://nodejs.org/download/rc/index.json</c>: <c>{"version":"v26.4.0-rc.0","date":"2026-06-23"}</c>.</summary>
    public static IReadOnlyList<PlatformPreview> ReadNodeCandidates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var previews = new List<PlatformPreview>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (String(release, "version") is not { } version || NodeRc().Match(version) is not { Success: true } match) continue;
            previews.Add(new PlatformPreview(match.Groups[1].Value, version.TrimStart('v'), Date(release, "date")));
        }
        return Newest(previews);
    }

    /// <summary>python.org's <c>/api/v2/downloads/release/?pre_release=true</c>: <c>{"name":"Python 3.16.0a1","release_date":"…"}</c>.</summary>
    public static IReadOnlyList<PlatformPreview> ReadPythonPrereleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var previews = new List<PlatformPreview>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (IsFalse(release, "pre_release")) continue;
            if (String(release, "name") is not { } name || PythonPre().Match(name) is not { Success: true } match) continue;
            var released = String(release, "release_date") is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? DateTime.SpecifyKind(at.Date, DateTimeKind.Utc) : (DateTime?)null;
            previews.Add(new PlatformPreview(match.Groups[2].Value, match.Groups[1].Value, released));
            // (python.org gives a full ISO timestamp, so it is parsed loosely; the other feeds give a bare day.)
        }
        return Newest(previews);
    }

    /// <summary>php.net's <c>pre-release-builds.php?format=json</c> (formerly <c>release-candidates.php</c>): per version a <c>release</c> with <c>version</c>
    /// (<c>8.6.0RC5</c>) and <c>date</c> (<c>05 November 2026</c>). A withdrawn one says <c>"enabled": false</c>; a
    /// published one has no <c>enabled</c> at all (capture of 2026-11-09), so only an explicit false is skipped.</summary>
    public static IReadOnlyList<PlatformPreview> ReadPhpCandidates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var previews = new List<PlatformPreview>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.Object) continue;
            if (IsFalse(release, "enabled") || String(release, "version") is not { } version || PhpRc().Match(version) is not { Success: true } match) continue;
            var released = Day(String(release, "date"), "dd MMMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "d MMM yyyy");
            previews.Add(new PlatformPreview(match.Groups[1].Value, version, released));
        }
        return Newest(previews);
    }

    /// <summary>ruby-lang.org's <c>_data/releases.yml</c>: <c>- version: 4.1.0-preview1</c> then <c>date: 2026-10-12</c>.
    /// Only previews and release candidates are taken; released versions come from endoflife.date.</summary>
    public static IReadOnlyList<PlatformPreview> ReadRubyPreviews(string yaml)
    {
        var previews = new List<PlatformPreview>();
        foreach (Match match in RubyRelease().Matches(yaml))
        {
            previews.Add(new PlatformPreview(match.Groups[2].Value, match.Groups[1].Value, Day(match.Groups[3].Value, "yyyy-MM-dd")));
        }
        return Newest(previews);
    }

    /// <summary>
    /// Rust's <c>channel-rust-beta.toml</c>: the manifest <c>date</c> and <c>[pkg.rust] version = "1.100.0-beta.13 (… 2026-11-07)"</c>.
    /// A beta is pinned by its dated toolchain (<c>beta-2026-11-08</c>, #606 D11), which becomes the row's name.
    /// </summary>
    public static IReadOnlyList<PlatformPreview> ReadRustBeta(string toml)
    {
        if (RustBeta().Match(toml) is not { Success: true } match) return [];
        var date = RustManifestDate().Match(toml) is { Success: true } d ? d.Groups[1].Value : null;
        return [new PlatformPreview(match.Groups[2].Value, match.Groups[1].Value, Day(date, "yyyy-MM-dd"), date is null ? null : $"beta-{date}")];
    }

    /// <summary>One preview per channel (the feed's first, which every feed lists newest first), newest channel first.</summary>
    private static List<PlatformPreview> Newest(IEnumerable<PlatformPreview> previews)
        => [.. previews.Where(p => IsChannel(p.Channel)).GroupBy(p => p.Channel).Select(g => g.OrderByDescending(p => p.ReleasedOn ?? DateTime.MinValue).First())
            .OrderByDescending(p => p.Channel, ChannelComparer.Instance)];

    /// <summary>A channel token: one or two runs of at most four digits (<c>24</c>, <c>10.0</c>, <c>1.100</c>). It lands in a
    /// document id, so nothing else is taken.</summary>
    public static bool IsChannel(string text) => ChannelToken().IsMatch(text);

    /// <summary>The leading number of <c>22</c>, <c>10.0</c> or <c>23.0.0-next.0</c>.</summary>
    internal static int? MajorOf(string text)
    {
        var end = 0;
        while (end < text.Length && end < 9 && char.IsAsciiDigit(text[end])) end++;
        return end > 0 && (end == text.Length || !char.IsAsciiDigit(text[end]))
            && int.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > 0
            ? major
            : null;
    }

    /// <summary>Orders channels numerically, segment by segment: 1.100 above 1.99.</summary>
    public sealed class ChannelComparer : IComparer<string>
    {
        public static readonly ChannelComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? string.Empty).Split('.');
            var b = (y ?? string.Empty).Split('.');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var left = i < a.Length && long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : 0;
                var right = i < b.Length && long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
                if (left != right) return left.CompareTo(right);
            }
            return 0;
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The property is there and says false; a missing one is not false.</summary>
    private static bool IsFalse(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static DateTime? Date(JsonElement element, string name) => Day(String(element, name), "yyyy-MM-dd");

    /// <summary>A UTC date in one of <paramref name="formats"/>, or null.</summary>
    private static DateTime? Day(string? text, params string[] formats)
        => text is not null && DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;

    [GeneratedRegex(@"^\d{1,4}(\.\d{1,4})?$")]
    private static partial Regex ChannelToken();

    [GeneratedRegex(@"^v(\d{1,4})\.\d+\.\d+-rc\.\d+$")]
    private static partial Regex NodeRc();

    [GeneratedRegex(@"^Python ((\d{1,4}\.\d{1,4})\.\d+(?:a|b|rc)\d+)$")]
    private static partial Regex PythonPre();

    [GeneratedRegex(@"^(\d{1,4}\.\d{1,4})\.\d+(?:RC|beta|alpha)\d+$")]
    private static partial Regex PhpRc();

    [GeneratedRegex(@"^- version: ((\d{1,4}\.\d{1,4})\.\d+-(?:preview|rc)\d+)\s*\r?\n(?:[ \t]+\S.*\r?\n)*?[ \t]+date: (\d{4}-\d{2}-\d{2})", RegexOptions.Multiline)]
    private static partial Regex RubyRelease();

    [GeneratedRegex(@"\[pkg\.rust\]\s*\r?\nversion = ""((\d{1,4}\.\d{1,4})\.\d+-beta\.\d+)")]
    private static partial Regex RustBeta();

    [GeneratedRegex(@"^date = ""(\d{4}-\d{2}-\d{2})""", RegexOptions.Multiline)]
    private static partial Regex RustManifestDate();
}
