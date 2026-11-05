using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #570 D11: a versioning run records which repository releases each package and what each release brought; a backport
/// whose change raised a first-party package above the line's major names that release, and per pull request whether the
/// upstream already backported it to a line of the same major. A private upstream is never named.
/// </summary>
public class UpstreamReleasesTests : CoverageRavenTest
{
    private const string Upstream = "Repositories/1";
    private const string Downstream = "Repositories/2";

    private static readonly GitWriterDependencyRaise Raise = new("package.json", "npm", "@mintplayer/ng-bootstrap", "^22.34.0", "^22.36.0", "^21.4.0");

    private readonly FakeForgeVersioning forge = new();

    [Theory]
    [InlineData("^22.36.0", "22.36.0")]
    [InlineData("~22.36", "22.36.0")]
    [InlineData("[22.36.0,)", "22.36.0")]
    [InlineData(">=22.36.0-beta.1", "22.36.0-beta.1")]
    [InlineData("latest", null)]
    public void The_minimum_is_the_lowest_version_a_range_admits(string range, string? minimum)
        => UpstreamReleases.Minimum(range).Should().Be(minimum);

    [Theory]
    [InlineData("21.x", 21)]
    [InlineData("angular-21.x", 21)]
    [InlineData("release/net10.0", 10)]
    [InlineData("10", 10)]
    [InlineData("21.4.x", null)]
    [InlineData("main", null)]
    public void A_backported_label_names_its_line_by_the_trailing_major(string line, int? major)
        => UpstreamReleases.LineMajor(line).Should().Be(major);

    [Fact]
    public void A_release_notes_pull_requests_are_read_in_order_once()
        => UpstreamReleases.PullRequestsIn("- fix: a (#12)\n- feat: b (#9)\n- chore: a again (#12)\n- see #4 later").Should().Equal(12, 9);

    private static VersioningRunRelease Release(string version, string body, string outcome = "created") => new()
    {
        Id = "@mintplayer/ng-bootstrap", Kind = "npm", Version = version, Tag = $"@mintplayer/ng-bootstrap/v{version}", TagOutcome = outcome,
        Release = new VersioningRunReleaseNotes { Name = version, Body = body },
    };

    [Fact]
    public async Task The_first_repository_to_release_a_package_keeps_it_and_each_version_is_recorded_once()
    {
        using var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            await UpstreamReleases.RecordAsync(session, Upstream, [Release("21.5.0", "- fix: a (#40)"), Release("21.6.0", "- x (#41)", "conflict")], default);
            await session.SaveChangesAsync();
        }
        using (var session = store.OpenAsyncSession())
        {
            await UpstreamReleases.RecordAsync(session, Upstream, [Release("21.5.0", "- fix: other (#99)")], default);
            await UpstreamReleases.RecordAsync(session, Downstream, [Release("22.0.0", "- takeover (#1)")], default);
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        var record = await check.LoadAsync<PackageReleaseRecord>(PackageReleaseRecord.DocumentId("npm", "@MintPlayer/ng-bootstrap"));
        record.Repository.Should().Be(Upstream);
        record.Releases.Should().HaveCount(1, "a tag that conflicted is not in place, and another repository cannot take the package over");
        record.Releases[0].Version.Should().Be("21.5.0");
        record.Releases[0].PullRequests.Should().Equal(40);
    }

    private async Task<string> DiagnoseAsync(IDocumentStore store, bool upstreamPrivate, bool recorded = true, string repository = Upstream)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "mintplayer/ng-bootstrap", Provider = EForgeProvider.GitHub, IsPrivate = upstreamPrivate }, Upstream);
            await seed.StoreAsync(new Repository { FullName = "mintplayer/spark", Provider = EForgeProvider.GitHub }, Downstream);
            if (recorded)
                await UpstreamReleases.RecordAsync(seed, repository, [Release("22.36.0", "- feat: a (#50)\n- fix: b (#51)")], default);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var self = await session.LoadAsync<Repository>(Downstream);
        return await UpstreamReleases.DiagnoseAsync(session, forge, self, "master", "release/angular-21.x", [Raise], default);
    }

    private const string Intro = "\n\n`release/angular-21.x` keeps `@mintplayer/ng-bootstrap` at `^21.4.0`: the change raised it from `^22.34.0` to `^22.36.0` "
        + "on `master`, above this line's major, so the line probably needs what `22.36.0` brought, released on its own major (21).";

    [Fact]
    public async Task A_public_upstream_release_lists_its_pull_requests_and_which_are_backported_to_the_lines_major()
    {
        using var store = GetDocumentStore();
        forge.OtherPullRequests[50] = new(50, "feat: `a` @someone", null, "a", "s", "main", false, true, false, null, ["backported: angular-21.x"]);
        forge.OtherPullRequests[51] = new(51, "fix: b", null, "b", "s", "main", false, true, false, null, ["backported: angular-20.x"]);

        var text = await DiagnoseAsync(store, upstreamPrivate: false);

        text.Should().Be(Intro + " It was released as `@mintplayer/ng-bootstrap/v22.36.0` in `mintplayer/ng-bootstrap`, with:\n"
            + "- `mintplayer/ng-bootstrap#50` `feat: 'a' @someone`: backported to `angular-21.x`\n"
            + "- `mintplayer/ng-bootstrap#51` `fix: b`: not backported to a 21.x line yet");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_private_or_unknown_upstream_leaves_the_dependency_alone_named(bool upstreamPrivate, bool recorded)
    {
        using var store = GetDocumentStore();

        var text = await DiagnoseAsync(store, upstreamPrivate, recorded);

        text.Should().Be(Intro);
    }

    [Fact]
    public async Task A_package_this_repository_releases_names_its_own_pull_requests_by_number()
    {
        using var store = GetDocumentStore();

        var text = await DiagnoseAsync(store, upstreamPrivate: true, repository: Downstream);

        text.Should().Be(Intro + " It was released as `@mintplayer/ng-bootstrap/v22.36.0` in this repository, with:\n"
            + "- #50: not backported to a 21.x line yet\n- #51: not backported to a 21.x line yet");
    }

    [Fact]
    public async Task A_record_keeps_only_the_newest_releases()
    {
        using var store = GetDocumentStore();
        var releases = Enumerable.Range(0, PackageReleaseRecord.MaxReleases + 2).Select(i => Release($"22.{i}.0", "- x")).ToList();
        using (var session = store.OpenAsyncSession())
        {
            await UpstreamReleases.RecordAsync(session, Upstream, releases, default);
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        var record = await check.LoadAsync<PackageReleaseRecord>(PackageReleaseRecord.DocumentId("npm", "@mintplayer/ng-bootstrap"));
        record.Releases.Should().HaveCount(PackageReleaseRecord.MaxReleases);
        record.Releases[0].Version.Should().Be("22.2.0");
        record.Releases[^1].PullRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public async Task A_release_names_its_pull_requests_up_to_the_cap(int count)
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "mintplayer/ng-bootstrap", Provider = EForgeProvider.GitHub }, Upstream);
            await seed.StoreAsync(new Repository { FullName = "mintplayer/spark", Provider = EForgeProvider.GitHub }, Downstream);
            var notes = string.Concat(Enumerable.Range(1, count).Select(n => $"- change (#{n})\n"));
            await UpstreamReleases.RecordAsync(seed, Upstream, [Release("22.36.0", notes)], default);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();

        var text = await UpstreamReleases.DiagnoseAsync(session, forge, (await session.LoadAsync<Repository>(Downstream))!, "master", "release/angular-21.x", [Raise], default);

        if (count == 0)
            text.Should().EndWith(" It was released as `@mintplayer/ng-bootstrap/v22.36.0` in `mintplayer/ng-bootstrap`; its notes name no pull request.");
        else
        {
            text.Split('\n').Count(l => l.StartsWith("- `mintplayer/ng-bootstrap#", StringComparison.Ordinal)).Should().Be(UpstreamReleases.MaxPullRequests);
            text.Should().EndWith($"\n- and {count - UpstreamReleases.MaxPullRequests} more");
        }
    }

    [Fact]
    public async Task Nothing_raised_says_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        (await UpstreamReleases.DiagnoseAsync(session, forge, new Repository(), "master", "release/8.x", [], default)).Should().BeEmpty();
    }
}
