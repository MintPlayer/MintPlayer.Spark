using System.Text;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// R14's plan commit (docs/code-coverage/dependency-updates-PRD.md §13.11.3 #11): the dispatch ref must be
/// <c>expected_sha</c> plus one commit that adds or edits only the plan file, so the dispatched workflow is the default
/// branch's own. Checked on real commits.
/// </summary>
public class PlanCommitRuleTests : IDisposable
{
    private readonly BaseRepo repo = new();
    private readonly string expected;

    public PlanCommitRuleTests() => expected = repo.Commit(("package.json", "{}\n"), (".github/workflows/apply.yml", "on: workflow_dispatch\n"));

    public void Dispose() => repo.Dispose();

    private string Child(string parent, params (string Path, string Mode, string Content)[] extra)
    {
        var entries = new List<(string, string, byte[])>
        {
            ("package.json", "100644", Encoding.UTF8.GetBytes("{}\n")),
            (".github/workflows/apply.yml", "100644", Encoding.UTF8.GetBytes("on: workflow_dispatch\n")),
        };
        foreach (var (path, mode, content) in extra)
        {
            entries.RemoveAll(e => e.Item1 == path);
            entries.Add((path, mode, Encoding.UTF8.GetBytes(content)));
        }
        return repo.Commit(entries, parent);
    }

    [Fact]
    public async Task One_commit_adding_only_the_plan_file_is_accepted()
    {
        var plan = Child(expected, (DependencyFiles.PlanPath, "100644", "{\"plan\": []}\n"));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, plan)).Should().BeNull();
    }

    [Fact]
    public async Task A_plan_commit_that_also_touches_github_is_refused()
    {
        var plan = Child(expected, (DependencyFiles.PlanPath, "100644", "{}\n"), (".github/workflows/apply.yml", "100644", "on: workflow_dispatch\njobs: {}\n"));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, plan)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_plan_commit_touching_a_second_path_is_refused()
    {
        var plan = Child(expected, (DependencyFiles.PlanPath, "100644", "{}\n"), ("package.json", "100644", "{\"x\": 1}\n"));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, plan)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_plan_commit_on_another_parent_or_with_an_executable_plan_is_refused()
    {
        var other = Child(expected, ("README.md", "100644", "x\n"));
        var onOther = Child(other, ("README.md", "100644", "x\n"), (DependencyFiles.PlanPath, "100644", "{}\n"));
        var executable = Child(expected, (DependencyFiles.PlanPath, "100755", "{}\n"));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, onOther)).Should().NotBeNullOrWhiteSpace();
        (await PlanCommitRule.CheckAsync(repo.Git, expected, executable)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_plan_commit_that_only_edits_another_path_is_refused()
    {
        var plan = Child(expected, (".github/workflows/apply.yml", "100644", "on: push\n"));

        (await PlanCommitRule.CheckAsync(repo.Git, expected, plan)).Should().NotBeNullOrWhiteSpace();
    }
}
