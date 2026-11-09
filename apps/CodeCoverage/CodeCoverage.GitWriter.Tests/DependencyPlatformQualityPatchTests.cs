using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #617 D31 (docs/code-coverage/sdk-updates-PRD.md §12): a move to a release removes setup-dotnet's
/// <c>dotnet-quality: preview</c> line. The line rule admits that one removal (and pairs every other changed line 1:1 as
/// before), and the YAML comparison admits a setup-dotnet <c>with:</c> mapping that lost exactly that input. Patches are
/// written by real <c>git diff</c>.
/// </summary>
public class DependencyPlatformQualityPatchTests : IDisposable
{
    private const string Ci = ".github/workflows/ci.yml";
    private const string Group = "platform:dotnet 11.0";
    private const string RcSdk = "11.0.100-rc.2.26504.105";

    private static readonly PlanPlatform ToRelease = new()
    {
        Sdk = "dotnet", FromChannel = "11.0", ToChannel = "11.0", ToVersion = "11.0.0", SdkVersion = "11.0.100",
    };

    private const string Workflow =
        "name: ci\n" +
        "on: [pull_request]\n" +
        "jobs:\n" +
        "  build:\n" +
        "    runs-on: ubuntu-latest\n" +
        "    steps:\n" +
        "      - uses: actions/setup-dotnet@v6\n" +
        "        with:\n" +
        $"          dotnet-version: {RcSdk}\n" +
        "      - run: dotnet build\n" +
        "  pack:\n" +
        "    runs-on: ubuntu-latest\n" +
        "    steps:\n" +
        "      - uses: actions/setup-dotnet@v6\n" +
        "        with:\n" +
        "          dotnet-version: 11.0.x\n" +
        "          dotnet-quality: preview\n" +
        "      - run: dotnet pack\n";

    /// <summary>What applyPlatform.ts writes for <see cref="ToRelease"/> (previewToGa.test.ts).</summary>
    private static readonly string Released = Workflow
        .Replace(RcSdk, "11.0.100", StringComparison.Ordinal)
        .Replace("          dotnet-quality: preview\n", "", StringComparison.Ordinal);

    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static DependencyPlatformLocation Location(string kind, string value) => new("dotnet", Ci, kind, value);

    private static readonly DependencyPlatformLocation[] Reported =
        [Location("setup-action", RcSdk), Location("setup-action", "11.0.x"), Location("dotnet-quality", "preview")];

    private async Task<DependencyPatchVerdict> Validate(string before, string after, PlanPlatform move, params DependencyPlatformLocation[] locations)
    {
        var from = repo.Commit((Ci, before));
        var to = repo.Change(from, (Ci, after));
        var request = new DependencyPatchRequest([]) { Platforms = [new DependencyPlatformMove(Group, move, locations)], ClaimedAppliedGroups = [Group] };
        return await DependencyPatchValidator.ValidateAsync(repo.Diff(from, to), request, repo.Tree(from));
    }

    [Fact]
    public async Task A_move_to_the_release_moves_the_pin_and_removes_the_quality()
    {
        var verdict = await Validate(Workflow, Released, ToRelease, Reported);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
        verdict.RequiresWorkflowsPermission.Should().BeTrue();
    }

    [Fact]
    public async Task A_move_to_another_preview_may_not_remove_the_quality()
    {
        var rc3 = ToRelease with { ToVersion = "11.0.0-rc.3", SdkVersion = "11.0.100-rc.3.26522.148" };
        var after = Workflow.Replace(RcSdk, "11.0.100-rc.3.26522.148", StringComparison.Ordinal).Replace("          dotnet-quality: preview\n", "", StringComparison.Ordinal);

        var verdict = await Validate(Workflow, after, rc3, Reported);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    [Fact]
    public async Task A_quality_the_lookup_did_not_report_may_not_go()
    {
        var verdict = await Validate(Workflow, Released, ToRelease, Location("setup-action", RcSdk), Location("setup-action", "11.0.x"));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    [Fact]
    public async Task Another_line_removed_with_the_quality_is_refused()
    {
        var after = Released.Replace("      - run: dotnet pack\n", "", StringComparison.Ordinal);

        var verdict = await Validate(Workflow, after, ToRelease, Reported);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
    }

    [Fact]
    public async Task A_dotnet_quality_input_of_another_action_is_not_removable()
    {
        var other = Workflow.Replace("      - uses: actions/setup-dotnet@v6\n        with:\n          dotnet-version: 11.0.x\n",
            "      - uses: octo-org/setup-sdk@v1\n        with:\n          dotnet-version: 11.0.x\n", StringComparison.Ordinal);
        var after = other.Replace("          dotnet-quality: preview\n", "", StringComparison.Ordinal);

        var verdict = await Validate(other, after, ToRelease, Reported);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(Jobs.PatchReasons.InvalidContent);
    }
}
