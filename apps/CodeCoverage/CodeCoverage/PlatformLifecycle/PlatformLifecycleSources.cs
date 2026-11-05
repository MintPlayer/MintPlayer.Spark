using System.Globalization;
using System.Text.Json;
using CodeCoverage.Entities;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>
/// Reads the platform lifecycle sources (#568 S10, PRD §8.1) into <see cref="PlatformRelease"/> rows. Pure: the
/// collector fetches, these only parse, against the real responses committed as test fixtures.
/// </summary>
public static class PlatformLifecycleSources
{
    public const string Dotnet = "dotnet";

    /// <summary>
    /// The .NET release index: one row per major, from its highest channel (3.1 over 3.0). <c>go-live</c> (the release
    /// candidates) is still a preview. The index names no GA date, so <see cref="PlatformRelease.ReleasedOn"/> stays null.
    /// </summary>
    public static IReadOnlyList<PlatformRelease> ReadDotnet(string json, string source, DateTime collectedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new Dictionary<int, (Version Channel, PlatformRelease Row)>();
        foreach (var release in document.RootElement.GetProperty("releases-index").EnumerateArray())
        {
            if (String(release, "channel-version") is not { } name || !Version.TryParse(name, out var channel)) continue;
            if (rows.TryGetValue(channel.Major, out var known) && known.Channel > channel) continue;
            rows[channel.Major] = (channel, new PlatformRelease
            {
                Id = PlatformRelease.IdFor(Dotnet, channel.Major),
                Platform = Dotnet,
                Major = channel.Major,
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
                Source = source,
                CollectedAtUtc = collectedAtUtc,
            });
        }
        return [.. rows.Values.Select(r => r.Row).OrderByDescending(r => r.Major)];
    }

    /// <summary>
    /// An endoflife.date v1 product. The phase comes from its booleans: <c>isEol</c> ends support, <c>isEoas</c> ends
    /// active support. <c>isMaintained</c> is not used: it stays true while only commercial support is left (Angular 9).
    /// </summary>
    public static IReadOnlyList<PlatformRelease> ReadEndOfLife(string platform, string json, string source, DateTime collectedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<PlatformRelease>();
        foreach (var release in document.RootElement.GetProperty("result").GetProperty("releases").EnumerateArray())
        {
            if (String(release, "name") is not { } name || MajorOf(name) is not { } major) continue;
            if (rows.Any(r => r.Major == major)) continue;
            rows.Add(new PlatformRelease
            {
                Id = PlatformRelease.IdFor(platform, major),
                Platform = platform,
                Major = major,
                Name = name,
                Phase = Bool(release, "isEol") ? "eol" : Bool(release, "isEoas") ? "maintenance" : "active",
                IsLts = Bool(release, "isLts"),
                ReleasedOn = Date(release, "releaseDate"),
                ActiveSupportEndsOn = Date(release, "eoasFrom"),
                SupportEndsOn = Date(release, "eolFrom"),
                LatestVersion = release.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object ? String(latest, "name") : null,
                Source = source,
                CollectedAtUtc = collectedAtUtc,
            });
        }
        return [.. rows.OrderByDescending(r => r.Major)];
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

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement element, string name)
        => String(element, name) is { } text && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;
}
