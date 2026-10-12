using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeCoverage.GitMirror.Tests._Infrastructure;
using CodeCoverage.GitMirror.Tests.Mirror;

namespace CodeCoverage.GitMirror.Tests;

/// <summary>The sidecar contract's status codes and shapes (scratchpad contract; PRD §4.1): 400 for anything that fails
/// validation, 404 missing / not-found, 410 stale, and the page fields the app reads.</summary>
public class ContractTests : MirrorTestBase
{
    [Theory]
    [InlineData("/repos/GitHub/1/graph")]
    [InlineData("/repos/git-hub/1/graph")]
    [InlineData("/repos/github/a.b/graph")]
    [InlineData("/repos/github/x%2F..%2Fy/graph")]
    [InlineData("/repos/github/1/graph?count=0")]
    [InlineData("/repos/github/1/graph?count=201")]
    [InlineData("/repos/github/1/graph?from=-1")]
    [InlineData("/repos/github/1/graph?from=x")]
    [InlineData("/repos/github/1/graph?at=abc")]
    [InlineData("/repos/github/1/graph?at=zzzzzzz")]
    [InlineData("/repos/github/1/graph?ref=main")]
    [InlineData("/repos/github/1/graph?ref=refs/heads/../x")]
    [InlineData("/repos/github/1/graph?ref=-refs/heads/x")]
    [InlineData("/repos/github/1/graph?firstParent=maybe")]
    [InlineData("/repos/github/1/graph?snapshot=NOT-HEX")]
    public async Task Invalid_graph_requests_are_400(string url)
    {
        (await Sidecar.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_repository_id_longer_than_64_characters_is_400()
    {
        (await Sidecar.Client.GetAsync($"/repos/github/{new string('a', 65)}/graph")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Sidecar.Client.DeleteAsync($"/repos/github/{new string('a', 65)}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_search_longer_than_200_characters_or_with_control_characters_is_400()
    {
        (await Sidecar.Client.GetAsync($"/repos/github/1/graph?grep={new string('a', 201)}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Sidecar.Client.GetAsync("/repos/github/1/graph?author=a%0Ab")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("ssh://git@example.invalid/o/r.git")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("https://user:secret@example.invalid/o/r.git")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_fetch_with_a_clone_url_that_is_not_plain_https_is_400(string url)
    {
        var response = await Sidecar.Client.PostAsJsonAsync("/repos/github/1/fetch", new { cloneUrl = url });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sidecar.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fetch_with_a_malformed_body_or_credential_is_400()
    {
        var malformed = await Sidecar.Client.PostAsync("/repos/github/1/fetch", new StringContent("{not json", Encoding.UTF8, "application/json"));
        var colon = await Sidecar.Client.PostAsJsonAsync("/repos/github/1/fetch", new { cloneUrl = "https://example.invalid/o/r.git", credential = new { username = "a:b", password = "x" } });
        var empty = await Sidecar.Client.PostAsJsonAsync("/repos/github/1/fetch", new { cloneUrl = "https://example.invalid/o/r.git", credential = new { username = "a", password = "" } });

        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        colon.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sidecar.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_graph_of_a_repository_without_a_mirror_is_404_missing()
    {
        var response = await Sidecar.Client.GetAsync("/repos/github/404/graph");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"state\":\"missing\"}");
    }

    [Fact]
    public async Task A_page_carries_the_contract_fields_and_all_refs_on_the_first_page()
    {
        var shas = CreateOrigin("repo");
        var fetched = await Sidecar.FetchAsync("github", "4001", Server.UrlOf("repo"));

        using var page = await Sidecar.GraphAsync("github", "4001", "?count=2");
        var root = page.RootElement;
        root.GetProperty("snapshotId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("refsHash").GetString().Should().Be(fetched.RefsHash);
        root.GetProperty("totalRows").GetInt64().Should().Be(4);
        root.GetProperty("from").GetInt32().Should().Be(0);
        root.GetProperty("laneCount").GetInt32().Should().Be(2);
        root.GetProperty("top").GetArrayLength().Should().Be(0);
        var row = root.GetProperty("rows")[0];
        row.GetProperty("sha").GetString().Should().Be(shas["d"]);
        row.GetProperty("parents")[0].GetString().Should().Be(shas["b"]);
        row.GetProperty("authorName").GetString().Should().Be("Test Author");
        row.GetProperty("authorEmail").GetString().Should().Be("author@example.invalid");
        // Strict ISO 8601 (%aI); git 2.47 writes UTC as "Z", older versions as "+00:00".
        DateTimeOffset.Parse(row.GetProperty("date").GetString()!).Should().Be(new DateTimeOffset(2026, 1, 1, 0, 4, 0, TimeSpan.Zero));
        row.GetProperty("subject").GetString().Should().Be("d");
        root.GetProperty("rows").GetArrayLength().Should().Be(2);
        root.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["refs/heads/feature", "refs/heads/main", "refs/tags/light", "refs/tags/v1"]);

        // A later page carries the lanes at its top edge and only the refs on its own rows.
        using var second = await Sidecar.GraphAsync("github", "4001", "?from=2&count=2");
        second.RootElement.GetProperty("top").EnumerateArray().Select(t => $"{t[0].GetInt32()}:{t[1].GetInt32()}").Should().Equal(["0:0"]);
        second.RootElement.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["refs/tags/light", "refs/tags/v1"]);
    }

    [Fact]
    public async Task At_returns_the_page_that_contains_the_commit_and_404_when_it_is_not_in_the_walk()
    {
        var shas = CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "4002", Server.UrlOf("repo"));

        using var page = await Sidecar.GraphAsync("github", "4002", $"?count=2&from=0&at={shas["a"][..9]}");
        page.RootElement.GetProperty("from").GetInt32().Should().Be(2);
        page.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("sha").GetString()).Should().Contain(shas["a"]);

        // d is not on main's walk; and an unknown SHA is not anywhere.
        var notInWalk = await Sidecar.Client.GetAsync($"/repos/github/4002/graph?ref=refs/heads/main&at={shas["d"]}");
        notInWalk.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await notInWalk.Content.ReadAsStringAsync()).Should().Be("{\"state\":\"not-found\"}");
        (await Sidecar.Client.GetAsync("/repos/github/4002/graph?at=0000000")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Sidecar.Client.GetAsync("/repos/github/4002/graph?ref=refs/heads/absent")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_ref_filter_walks_only_that_refs_ancestry()
    {
        var shas = CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "4003", Server.UrlOf("repo"));

        using var page = await Sidecar.GraphAsync("github", "4003", "?ref=refs/heads/feature");

        page.RootElement.GetProperty("totalRows").GetInt64().Should().Be(3);
        page.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("sha").GetString())
            .Should().Equal([shas["d"], shas["b"], shas["a"]]);
    }

    [Fact]
    public async Task A_snapshot_id_of_the_current_refs_is_served_and_any_other_is_410()
    {
        CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "4004", Server.UrlOf("repo"));
        using var first = await Sidecar.GraphAsync("github", "4004");
        var id = first.RootElement.GetProperty("snapshotId").GetString()!;

        (await Sidecar.Client.GetAsync($"/repos/github/4004/graph?snapshot={id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await Sidecar.Client.GetAsync($"/repos/github/4004/graph?snapshot={new string('a', 32)}");
        stale.StatusCode.Should().Be(HttpStatusCode.Gone);
        using var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("snapshotId").GetString().Should().Be(id);
    }

    [Fact]
    public async Task Healthz_and_stats_answer()
    {
        CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "4005", Server.UrlOf("repo"));

        (await Sidecar.Client.GetStringAsync("/healthz")).Should().Be("ok");
        using var stats = JsonDocument.Parse(await Sidecar.Client.GetStringAsync("/stats"));
        stats.RootElement.GetProperty("mirrorCount").GetInt32().Should().Be(1);
        stats.RootElement.GetProperty("mirrorBytes").GetInt64().Should().BeGreaterThan(0);
        stats.RootElement.GetProperty("freeBytes").GetInt64().Should().BeGreaterThan(0);
        stats.RootElement.GetProperty("totalBytes").GetInt64().Should().BeGreaterThan(stats.RootElement.GetProperty("freeBytes").GetInt64());
    }
}

/// <summary>Production accepts https clone URLs only; http is a test seam (<c>GitMirror:AllowInsecureHttp</c>).</summary>
public class HttpsOnlyTests : MirrorTestBase
{
    protected override IReadOnlyDictionary<string, string?>? SidecarSettings => new Dictionary<string, string?>
    {
        ["GitMirror:AllowInsecureHttp"] = "false",
    };

    [Fact]
    public async Task An_http_clone_url_is_400_by_default()
    {
        CreateOrigin("repo");

        var response = await Sidecar.Client.PostAsJsonAsync("/repos/github/1/fetch", new { cloneUrl = Server.UrlOf("repo").AbsoluteUri });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sidecar.Started.Should().BeEmpty();
    }
}
