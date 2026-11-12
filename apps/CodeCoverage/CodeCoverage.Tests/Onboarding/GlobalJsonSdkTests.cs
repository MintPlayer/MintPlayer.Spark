using CodeCoverage.Git;
using CodeCoverage.Onboarding;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// #649 D11-D15: the opt-in "SDK from global.json" conversion. The workflow is SpaServices' <c>pull-request.yml</c> as it is on
/// master (2026-11-12): two jobs, each <c>actions/checkout@v7</c> then <c>actions/setup-dotnet@v6</c> pinning the global.json SDK.
/// </summary>
public class GlobalJsonSdkTests
{
    private const string Workflow = ".github/workflows/pull-request.yml";

    private const string SpaGlobalJson = "{\n\t\"sdk\": {\n\t\t\"version\": \"11.0.100-rc.1.26425.128\",\n\t\t\"rollForward\": \"latestFeature\"\n\t},\n"
        + "\t\"tools\": {\n\t\t\"dotnet\": \"11.0.100-rc.1.26425.128\"\n\t}\n}\n";

    private const string SpaPullRequest = """
        name: pull-request

        on:
          pull_request:
            branches: [ master, 'release/**' ]

        jobs:
          build:

            name: pull-request
            runs-on: ubuntu-latest

            steps:
            - name: Checkout
              uses: actions/checkout@v7
              # Full history: MintPlayer.Versioning.MSBuild reads each version from the tags reachable from HEAD.
              with:
                fetch-depth: 0

            - name: Setup .NET Core
              uses: actions/setup-dotnet@v6
              with:
                dotnet-version: 11.0.100-rc.1.26425.128

            - name: Build
              run: dotnet build

          node-versions:

            name: node ${{ matrix.node }}
            runs-on: ubuntu-latest

            steps:
            - name: Checkout
              uses: actions/checkout@v7
              with:
                fetch-depth: 0

            - name: Setup .NET Core
              uses: actions/setup-dotnet@v6
              with:
                dotnet-version: 11.0.100-rc.1.26425.128

        """;

    [Fact]
    public void D14_every_scalar_pin_after_a_checkout_reads_global_json()
    {
        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = SpaPullRequest });

        conversion.Notes.Should().BeEmpty();
        conversion.Files.Should().ContainSingle().Which.Content.Should().Be(SpaPullRequest.Replace(
            "dotnet-version: 11.0.100-rc.1.26425.128", "global-json-file: global.json", StringComparison.Ordinal));
        conversion.Changes.Should().Equal(
            $"`{Workflow}:23`: dotnet-version `11.0.100-rc.1.26425.128` → `global-json-file: global.json`",
            $"`{Workflow}:42`: dotnet-version `11.0.100-rc.1.26425.128` → `global-json-file: global.json`");
    }

    [Fact]
    public void Planning_again_on_the_result_changes_nothing()
    {
        var first = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = SpaPullRequest });

        var again = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = first.Files[0].Content! });

        again.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void D13_without_sdk_version_in_global_json_nothing_converts_and_a_note_says_why()
    {
        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { [Workflow] = SpaPullRequest });

        conversion.Files.Should().BeEmpty();
        conversion.Notes.Should().ContainSingle().Which.Should().Contain("no `global.json` with an `sdk.version`");
    }

    [Fact]
    public void S11_a_step_before_the_job_s_checkout_keeps_its_pin()
    {
        const string text = "jobs:\n  build:\n    steps:\n    - uses: actions/setup-dotnet@v6\n      with:\n        dotnet-version: 10.0.x\n    - uses: actions/checkout@v7\n";

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = text });

        conversion.Files.Should().BeEmpty();
        conversion.Notes.Should().ContainSingle().Which.Should().StartWith($"`{Workflow}:6` installs the SDK before the job checks out");
    }

    [Fact]
    public void S11_another_job_s_checkout_does_not_count()
    {
        const string text = "jobs:\n  a:\n    steps:\n    - uses: actions/checkout@v7\n  b:\n    steps:\n    - uses: actions/setup-dotnet@v6\n      with:\n        dotnet-version: 10.0.x\n";

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = text });

        conversion.Files.Should().BeEmpty();
        conversion.Notes.Should().ContainSingle();
    }

    [Fact]
    public void D15_a_block_list_loses_global_json_s_major_and_keeps_the_lower_entries()
    {
        const string text = "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v7\n    - uses: actions/setup-dotnet@v6\n      with:\n"
            + "        dotnet-version: |\n          10.0.x\n          11.0.x\n    - run: dotnet test\n";

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = text });

        conversion.Files.Should().ContainSingle().Which.Content.Should().Be("jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v7\n    - uses: actions/setup-dotnet@v6\n      with:\n"
            + "        dotnet-version: |\n          10.0.x\n        global-json-file: global.json\n    - run: dotnet test\n");
        conversion.Changes.Should().ContainSingle().Which.Should().Contain("keeps `10.0.x`");
    }

    [Fact]
    public void D15_a_block_list_of_global_json_s_major_only_becomes_the_key()
    {
        const string text = "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v7\n    - uses: actions/setup-dotnet@v6\n      with:\n"
            + "        dotnet-version: |\n          11.0.x\n    - run: dotnet test\n";

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = text });

        conversion.Files.Should().ContainSingle().Which.Content.Should().Be("jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v7\n    - uses: actions/setup-dotnet@v6\n      with:\n"
            + "        global-json-file: global.json\n    - run: dotnet test\n");
    }

    [Fact]
    public void A_matrix_expression_is_left_for_a_person()
    {
        const string text = "jobs:\n  build:\n    steps:\n    - uses: actions/checkout@v7\n    - uses: actions/setup-dotnet@v6\n      with:\n        dotnet-version: ${{ matrix.dotnet }}\n";

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = text });

        conversion.Files.Should().BeEmpty();
        conversion.Notes.Should().ContainSingle().Which.Should().Contain("does not rewrite");
    }

    [Fact]
    public void Crlf_workflows_keep_their_line_endings()
    {
        var crlf = SpaPullRequest.ReplaceLineEndings("\r\n");

        var conversion = GlobalJsonSdk.Plan(new Dictionary<string, string> { ["global.json"] = SpaGlobalJson, [Workflow] = crlf });

        conversion.Files.Should().ContainSingle().Which.Content.Should().Be(crlf.Replace(
            "dotnet-version: 11.0.100-rc.1.26425.128", "global-json-file: global.json", StringComparison.Ordinal));
    }

    /// <summary>#649 D7: every file a narrowing writes is one the git writer's narrowing accepts.</summary>
    [Fact]
    public void D7_a_narrowing_with_workflows_writes_only_what_the_writer_accepts()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            ["src/Spa/Spa.csproj"] = "<Project><PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup></Project>\n",
            ["global.json"] = SpaGlobalJson,
            [Workflow] = SpaPullRequest,
        }, latestSdk: "10.0.203", latestRuntime: "10.0.7");

        narrowing.Files.Select(f => f.Path).Should().Contain(Workflow);
        narrowing.Files.Should().OnlyContain(f => LinePaths.IsNarrowable(f.Path));
    }
}
