using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// #570 D11, cross-repo first-party dependencies: diagnose and point the way. A backport whose change raised a
/// first-party package above the line's major keeps the line's own version (normalization drops the raise), so the line
/// probably lacks what the newer upstream release brought. The report names that release, lists its pull requests and
/// shows per pull request whether the upstream repository already backported it to a line of the same major. It never
/// starts a backport in the other repository: that is the upstream maintainers' call.
/// </summary>
/// <remarks>
/// The upstream is named only when this instance knows it (<see cref="PackageReleaseRecord"/>) and it is public or the
/// same repository: a private repository's name, release or pull requests never reach another repository's comment.
/// Otherwise the dependency alone is named. Pull requests are written as code (<c>`owner/repo#N`</c>), not links, so the
/// comment adds no cross-reference to the upstream pull requests' timelines.
/// </remarks>
public static partial class UpstreamReleases
{
    public const int MaxRaises = 5;
    public const int MaxPullRequests = 10;

    [GeneratedRegex(@"\(#(?<n>[0-9]{1,9})\)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex PullRequestReference();

    [GeneratedRegex(@"[0-9]+(\.[0-9]+){1,2}(-[0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionText();

    [GeneratedRegex(@"[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"(?<![0-9.])(?<major>[0-9]{1,6})(\.x|\.0)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex TrailingMajor();

    /// <summary>The pull requests a release's notes name, in order, without duplicates.</summary>
    public static List<int> PullRequestsIn(string? notes)
        => notes is null ? [] : PullRequestReference().Matches(notes).Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture))
            .Distinct().Take(PackageReleaseRecord.MaxPullRequests).ToList();

    /// <summary>The lowest version a range or version admits, as a release is versioned: <c>^22.36.0</c>, <c>~22.36</c>,
    /// <c>[22.36.0,)</c> and <c>&gt;=22.36.0</c> all give <c>22.36.0</c>. Null when it names none.</summary>
    public static string? Minimum(string range)
    {
        var match = VersionText().Match(range);
        if (!match.Success) return null;
        return match.Groups[1].Captures.Count == 1 && !match.Groups[2].Success ? match.Value + ".0" : match.Value;
    }

    /// <summary>The major of a value: the first number of <c>^21.4.0</c>.</summary>
    public static int? MajorOf(string value)
    {
        var match = Number().Match(value);
        return match.Success && match.Value.Length <= 6 ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The major a line name ends in: <c>21.x</c>, <c>angular-21.x</c>, <c>net10.0</c>; null for anything else.</summary>
    public static int? LineMajor(string line)
        => TrailingMajor().Match(line) is { Success: true } match ? int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Records the releases a versioning run put in place, for later backports to find (D11). The first repository
    /// to release a package keeps it.</summary>
    public static async Task RecordAsync(IAsyncDocumentSession session, string repositoryId, IEnumerable<VersioningRunRelease> releases,
        CancellationToken cancellationToken)
    {
        foreach (var release in releases)
        {
            if (release.TagOutcome is not ("created" or "already-present") || release.Id.Length == 0 || release.Version.Length == 0) continue;
            if (release.Kind is not ("npm" or "nuget")) continue;
            var id = PackageReleaseRecord.DocumentId(release.Kind, release.Id);
            var record = await session.LoadAsync<PackageReleaseRecord>(id, cancellationToken);
            if (record is null)
            {
                record = new PackageReleaseRecord { Id = id, Repository = repositoryId, Kind = release.Kind, Package = release.Id };
                await session.StoreAsync(record, id, cancellationToken);
            }
            else if (!string.Equals(record.Repository, repositoryId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (record.Releases.Any(r => string.Equals(r.Version, release.Version, StringComparison.OrdinalIgnoreCase))) continue;
            record.Releases.Add(new PackageReleaseEntry { Version = release.Version, Tag = release.Tag, PullRequests = PullRequestsIn(release.Release?.Body) });
            if (record.Releases.Count > PackageReleaseRecord.MaxReleases)
                record.Releases.RemoveRange(0, record.Releases.Count - PackageReleaseRecord.MaxReleases);
        }
    }

    /// <summary>The "probably needs" section of one line's report; empty when the change raised nothing.</summary>
    public static async Task<string> DiagnoseAsync(IAsyncDocumentSession session, IForgeVersioning forge, Repository repository, string defaultBranch,
        string line, IReadOnlyList<GitWriterDependencyRaise>? raises, CancellationToken cancellationToken)
    {
        if (raises is not { Count: > 0 }) return string.Empty;
        var builder = new StringBuilder();
        foreach (var raise in raises.DistinctBy(r => (r.Kind, r.Package.ToLowerInvariant(), r.After)).Take(MaxRaises))
        {
            var minimum = Minimum(raise.After);
            var major = MajorOf(raise.LineValue);
            builder.Append(CultureInfo.InvariantCulture,
                $"\n\n`{line}` keeps `{raise.Package}` at `{raise.LineValue}`: the change raised it from `{raise.Before}` to `{raise.After}` on `{defaultBranch}`, "
                + $"above this line's major, so the line probably needs what {(minimum is null ? "that version" : $"`{minimum}`")} brought, released on its own major{(major is null ? "" : $" ({major})")}.");

            if (minimum is null || await UpstreamAsync(session, repository, raise.Kind, raise.Package, cancellationToken) is not { } upstream
                || upstream.Record.Releases.FirstOrDefault(r => string.Equals(r.Version, minimum, StringComparison.OrdinalIgnoreCase)) is not { } release)
                continue;

            var self = upstream.Repository.Id == repository.Id;
            var name = self ? "this repository" : $"`{upstream.Repository.FullName}`";
            if (release.PullRequests.Count == 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $" It was released as `{release.Tag}` in {name}; its notes name no pull request.");
                continue;
            }
            builder.Append(CultureInfo.InvariantCulture, $" It was released as `{release.Tag}` in {name}, with:\n");
            foreach (var number in release.PullRequests.Take(MaxPullRequests))
            {
                var pull = await forge.GetPullRequestTextAsync(upstream.Repository, number, cancellationToken);
                var backported = pull?.Labels
                    .Where(l => l.StartsWith(BackportProvenance.BackportedPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l[BackportProvenance.BackportedPrefix.Length..].Trim())
                    .FirstOrDefault(l => major is { } m && LineMajor(l) == m);
                var reference = self ? $"#{number}" : $"`{upstream.Repository.FullName}#{number}`";
                // Another repository's title is untrusted text: a code span keeps its mentions and markdown inert.
                var title = pull is null ? "" : $" `{pull.Title.Replace('`', '\'').ReplaceLineEndings(" ")}`";
                var state = backported is null ? $"not backported to a {(major is null ? "line of this major" : $"{major}.x line")} yet" : $"backported to `{backported}`";
                builder.Append(CultureInfo.InvariantCulture, $"- {reference}{title}: {state}\n");
            }
            if (release.PullRequests.Count > MaxPullRequests)
                builder.Append(CultureInfo.InvariantCulture, $"- and {release.PullRequests.Count - MaxPullRequests} more\n");
        }
        return builder.ToString().TrimEnd('\n');
    }

    private sealed record Upstream(Repository Repository, PackageReleaseRecord Record);

    /// <summary>The repository that releases the package, when it may be named here: public, or this repository itself.</summary>
    private static async Task<Upstream?> UpstreamAsync(IAsyncDocumentSession session, Repository repository, string kind, string package,
        CancellationToken cancellationToken)
    {
        var record = await session.LoadAsync<PackageReleaseRecord>(PackageReleaseRecord.DocumentId(kind, package), cancellationToken);
        if (record is null) return null;
        var upstream = string.Equals(record.Repository, repository.Id, StringComparison.OrdinalIgnoreCase)
            ? repository
            : await session.LoadAsync<Repository>(record.Repository, cancellationToken);
        if (upstream is null || upstream.Provider != repository.Provider) return null;
        if (upstream.Id != repository.Id && (upstream.IsPrivate || upstream.Connection == RepositoryConnection.Disconnected)) return null;
        return new Upstream(upstream, record);
    }
}
