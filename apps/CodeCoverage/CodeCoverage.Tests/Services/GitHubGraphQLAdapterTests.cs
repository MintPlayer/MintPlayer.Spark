using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The Projects-V2 GraphQL adapters — board discovery, the Status field, and card moves — run
/// through a real <c>Octokit.GraphQL.Connection</c> over <see cref="StubGitHub"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>GitHubProjectCards</c> and <c>InstallationProjects</c> were at 0%: every consumer
/// substituted them. The responses below are shaped exactly as GitHub's GraphQL API answers the
/// queries Octokit.GraphQL generates (captured from the posted bodies): the <c>data</c> envelope,
/// the root field, the aliases the generator introduces (<c>projectId</c>, <c>itemId</c>,
/// <c>repoName</c>), <c>__typename</c> on interface and union selections, and <c>pageInfo</c> on
/// every connection read with <c>AllPages()</c> — without it the pager has nothing to stop on.
/// </para>
/// </remarks>
public class GitHubGraphQLAdapterTests
{
    private const string NoMorePages = """ "pageInfo": { "hasNextPage": false, "endCursor": "Y3Vyc29yOnYyOpHOAAAAAQ==" } """;

    private static GitHubProject Board(string? statusFieldId = "PVTSSF_status") => new()
    {
        NodeId = "PVT_board", InstallationId = 31, Number = 4, Name = "Roadmap", StatusFieldId = statusFieldId,
    };

    // ------------------------------------------------------------------------------------------
    // InstallationProjects
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "organization")]
    [InlineData(false, "user")]
    public async Task Boards_are_listed_through_the_owner_types_root_field_with_the_login_as_a_variable(bool organization, string root)
    {
        var github = new StubGitHub().OnGraphQL($$"""
            { "data": { "{{root}}": { "projectsV2": { "nodes": [
              { "id": "PVT_one", "number": 1, "title": "Roadmap", "closed": false },
              { "id": "PVT_two", "number": 2, "title": "Old", "closed": true }
            ] } } } }
            """);

        var boards = await new InstallationProjects(github).ListAsync(31, "acme", organization, max: 100);

        boards.Should().Equal(new InstallationProject("PVT_one", 1, "Roadmap", false), new InstallationProject("PVT_two", 2, "Old", true));
        var body = github.GraphQLBodies.Single();
        StubGitHub.QueryOf(body).Should().StartWith($"query($login:String!){{{root}(login:$login)");
        body.Should().Contain("\"variables\":{\"login\":\"acme\"}", "the login travels as a variable, never interpolated (defect B1)");
        github.InstallationClients.Should().Equal(31L);
    }

    [Fact]
    public async Task A_board_list_longer_than_the_maximum_is_cut_to_it()
    {
        var github = new StubGitHub().OnGraphQL("""
            { "data": { "organization": { "projectsV2": { "nodes": [
              { "id": "PVT_1", "number": 1, "title": "a", "closed": false },
              { "id": "PVT_2", "number": 2, "title": "b", "closed": false },
              { "id": "PVT_3", "number": 3, "title": "c", "closed": false }
            ] } } } }
            """);

        (await new InstallationProjects(github).ListAsync(31, "acme", true, max: 2)).Select(b => b.NodeId).Should().Equal("PVT_1", "PVT_2");
    }

    /// <summary>
    /// The Status field is the single-select field named Status; other fields come back as bare
    /// typenames and are skipped. Its options are the columns.
    /// </summary>
    [Fact]
    public async Task The_status_field_and_its_options_are_read_from_the_boards_fields()
    {
        var github = new StubGitHub().OnGraphQL($$"""
            { "data": { "node": { "__typename": "ProjectV2", "id": "PVT_board", "fields": {
              {{NoMorePages}},
              "nodes": [
                { "__typename": "ProjectV2Field" },
                { "__typename": "ProjectV2SingleSelectField", "id": "PVTSSF_priority", "name": "Priority", "options": [ { "id": "p1", "name": "High" } ] },
                { "__typename": "ProjectV2SingleSelectField", "id": "PVTSSF_status", "name": "Status", "options": [
                  { "id": "f75ad846", "name": "Todo" }, { "id": "47fc9ee4", "name": "In Progress" }, { "id": "98236657", "name": "Done" } ] },
                { "__typename": "ProjectV2IterationField" }
              ] } } } }
            """);

        var field = await new InstallationProjects(github).GetStatusFieldAsync(31, "PVT_board");

        field.Exists.Should().BeTrue();
        field.FieldId.Should().Be("PVTSSF_status");
        field.Columns.Should().Equal(new InstallationProjectColumn("f75ad846", "Todo"), new InstallationProjectColumn("47fc9ee4", "In Progress"), new InstallationProjectColumn("98236657", "Done"));
        StubGitHub.QueryOf(github.GraphQLBodies.Single()).Should().Contain("node(id:\"PVT_board\")");
    }

    [Fact]
    public async Task A_board_without_a_Status_field_reports_none()
    {
        var github = new StubGitHub().OnGraphQL($$"""
            { "data": { "node": { "__typename": "ProjectV2", "id": "PVT_board", "fields": { {{NoMorePages}}, "nodes": [ { "__typename": "ProjectV2Field" } ] } } } }
            """);

        (await new InstallationProjects(github).GetStatusFieldAsync(31, "PVT_board")).Should().Be(ProjectStatusField.None);
    }

    // ------------------------------------------------------------------------------------------
    // GitHubProjectCards
    // ------------------------------------------------------------------------------------------

    private static string IssueItems(string contentId, bool hasNextPage, params (string Project, string Item)[] items) => $$"""
        { "data": { "repository": { "issue": { "id": "{{contentId}}", "projectItems": {
          "pageInfo": { "hasNextPage": {{(hasNextPage ? "true" : "false")}}, "endCursor": "Y3Vyc29yOjE=" },
          "nodes": [{{string.Join(",", items.Select(i => $$"""{ "projectId": { "id": "{{i.Project}}" }, "itemId": "{{i.Item}}" }"""))}}]
        } } } } }
        """;

    private const string Moved = """{ "data": { "updateProjectV2ItemFieldValue": { "clientMutationId": "ok" } } }""";

    private static GitHubProjectCards Cards(StubGitHub github) => new(github, NullLogger<GitHubProjectCards>.Instance);

    /// <summary>
    /// The item is found on a later page — the pager follows <c>endCursor</c> through a node query —
    /// and moved by setting the Status field to the target option.
    /// </summary>
    [Fact]
    public async Task An_issue_already_on_the_board_is_moved_even_when_its_item_is_on_a_later_page()
    {
        var github = new StubGitHub()
            .OnGraphQL(IssueItems("I_kwDOissue", hasNextPage: true, ("PVT_other", "PVTI_elsewhere")))
            .OnGraphQL("""
                { "data": { "node": { "__typename": "Issue", "projectItems": {
                  "pageInfo": { "hasNextPage": false, "endCursor": "Y3Vyc29yOjI=" },
                  "nodes": [ { "projectId": { "id": "PVT_board" }, "itemId": "PVTI_here" } ] } } } }
                """)
            .OnGraphQL(Moved);

        var outcome = await Cards(github).MoveIssueAsync(Board(), "acme", "widget", 5, "47fc9ee4", addIfMissing: false);

        outcome.Should().Be(ECardOutcome.Moved);
        var bodies = github.GraphQLBodies;
        bodies.Should().HaveCount(3);
        bodies[1].Should().Contain("\"__after\":\"Y3Vyc29yOjE=\"");
        var mutation = StubGitHub.QueryOf(bodies[2]);
        mutation.Should().Contain("updateProjectV2ItemFieldValue");
        mutation.Should().Contain("PVTI_here");
        mutation.Should().Contain("PVTSSF_status");
        mutation.Should().Contain("singleSelectOptionId: \"47fc9ee4\"");
        mutation.Should().NotContain("null", "GitHub rejects the explicit nulls the generator emits for the other value kinds");
        mutation.Should().NotContain("\n");
    }

    [Fact]
    public async Task An_issue_not_on_the_board_is_left_alone_unless_adding_is_allowed()
    {
        var github = new StubGitHub().OnGraphQL(IssueItems("I_kwDOissue", hasNextPage: false, ("PVT_other", "PVTI_elsewhere")));

        (await Cards(github).MoveIssueAsync(Board(), "acme", "widget", 5, "opt", addIfMissing: false)).Should().Be(ECardOutcome.NotOnBoard);
        github.GraphQLBodies.Should().ContainSingle("nothing is written for a card that is not on the board");
    }

    [Fact]
    public async Task An_issue_not_on_the_board_is_added_then_moved_when_adding_is_allowed()
    {
        var github = new StubGitHub()
            .OnGraphQL(IssueItems("I_kwDOissue", hasNextPage: false))
            .OnGraphQL("""{ "data": { "repository": { "issue": { "id": "I_kwDOissue" } } } }""")
            .OnGraphQL("""{ "data": { "addProjectV2ItemById": { "item": { "id": "PVTI_added" } } } }""")
            .OnGraphQL(Moved);

        var outcome = await Cards(github).MoveIssueAsync(Board(), "acme", "widget", 5, "opt", addIfMissing: true);

        outcome.Should().Be(ECardOutcome.Added);
        var bodies = github.GraphQLBodies;
        StubGitHub.QueryOf(bodies[2]).Should().Contain("addProjectV2ItemById");
        StubGitHub.QueryOf(bodies[2]).Should().Contain("I_kwDOissue");
        StubGitHub.QueryOf(bodies[3]).Should().Contain("PVTI_added");
    }

    [Fact]
    public async Task A_pull_request_is_found_added_and_moved_the_same_way()
    {
        var github = new StubGitHub()
            .OnGraphQL($$"""
                { "data": { "repository": { "pullRequest": { "id": "PR_kwDOpull", "projectItems": { {{NoMorePages}}, "nodes": [
                  { "projectId": { "id": "PVT_board" }, "itemId": "PVTI_pr" } ] } } } } }
                """)
            .OnGraphQL(Moved)
            .OnGraphQL($$"""{ "data": { "repository": { "pullRequest": { "id": "PR_kwDOpull", "projectItems": { {{NoMorePages}}, "nodes": [] } } } } }""")
            .OnGraphQL("""{ "data": { "repository": { "pullRequest": { "id": "PR_kwDOpull" } } } }""")
            .OnGraphQL("""{ "data": { "addProjectV2ItemById": { "item": { "id": "PVTI_pr_added" } } } }""")
            .OnGraphQL(Moved)
            .OnGraphQL($$"""{ "data": { "repository": { "pullRequest": { "id": "PR_kwDOpull", "projectItems": { {{NoMorePages}}, "nodes": [] } } } } }""");
        var cards = Cards(github);

        (await cards.MovePullRequestAsync(Board(), "acme", "widget", 6, "opt", addIfMissing: false)).Should().Be(ECardOutcome.Moved);
        (await cards.MovePullRequestAsync(Board(), "acme", "widget", 6, "opt", addIfMissing: true)).Should().Be(ECardOutcome.Added);
        (await cards.MovePullRequestAsync(Board(), "acme", "widget", 6, "opt", addIfMissing: false)).Should().Be(ECardOutcome.NotOnBoard);
    }

    /// <summary>A board whose Status field was never synced cannot move a card, and says what to do about it.</summary>
    [Fact]
    public async Task A_board_without_a_cached_Status_field_refuses_to_move_a_card()
    {
        var github = new StubGitHub().OnGraphQL(IssueItems("I_kwDOissue", hasNextPage: false, ("PVT_board", "PVTI_here")));

        var ex = (await new Func<Task>(() => Cards(github).MoveIssueAsync(Board(statusFieldId: null), "acme", "widget", 5, "opt", addIfMissing: false)).Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        ex.Message.Should().Contain("Run Sync columns");
    }

    [Fact]
    public async Task The_issues_a_pull_request_closes_are_listed_with_their_repository()
    {
        var github = new StubGitHub().OnGraphQL($$"""
            { "data": { "repository": { "pullRequest": { "id": "PR_kwDOpull", "closingIssuesReferences": { {{NoMorePages}}, "nodes": [
              { "number": 12, "repoName": { "name": "widget" } },
              { "number": 3, "repoName": { "name": "docs" } } ] } } } } }
            """);

        var issues = await Cards(github).GetClosingIssuesAsync(31, "acme", "widget", 6);

        issues.Should().Equal(("widget", 12), ("docs", 3));
        github.InstallationClients.Should().Equal(31L);
    }
}
