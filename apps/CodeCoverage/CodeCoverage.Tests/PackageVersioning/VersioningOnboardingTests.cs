using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Graph;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #153 M13 (docs/coverage_package_versioning_PRD.md D17): the onboarding pull request's edits, on repositories shaped like
/// the two consumers (MintPlayer.Dotnet.Tools' publish-release job, mintplayer-ng-bootstrap's publish jobs), byte for byte.
/// </summary>
public class VersioningOnboardingTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly HashSet<string> None = [];

    private static Dictionary<string, string?> ByPath(VersioningOnboardingPlan plan) => plan.Files.ToDictionary(f => f.Path, f => f.Content);

    [Fact]
    public async Task A_dotnet_repository_gets_the_package_once_placeholders_and_the_step_before_its_pack()
    {
        var tree = new InMemoryTree()
            .Add("Directory.Build.props", """
                <Project>
                  <PropertyGroup>
                    <Authors>MintPlayer</Authors>
                  </PropertyGroup>
                </Project>

                """)
            .Add("src/Tools/Tools.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <PackageId>MintPlayer.Tools</PackageId>
                    <Version>11.2.0</Version>
                  </PropertyGroup>
                </Project>

                """)
            .Add("src/Assertions/Assertions.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <PackageId>MintPlayer.Assertions</PackageId>
                    <VersionPrefix>1.0.1</VersionPrefix>
                    <VersionSuffix>rc.4</VersionSuffix>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="MinVer" Version="6.0.0" PrivateAssets="all" />
                  </ItemGroup>
                </Project>

                """)
            .Add(".github/workflows/publish.yml", """
                name: publish-release

                on:
                  push:
                    branches:
                      - 'master'

                permissions:
                  contents: read
                  packages: write

                jobs:
                  build:
                    name: publish-release
                    runs-on: ubuntu-latest

                    steps:
                    - name: Checkout
                      uses: actions/checkout@v5

                    - name: Test
                      # --no-build: the Release build is the one tested.
                      run: dotnet test --no-build --configuration Release

                    - name: Pack
                      run: dotnet pack --no-build --configuration Release /p:ContinuousIntegrationBuild=true

                    # The --no-build pack once shipped 99.9.9.0 (#187): check the packages before anything is pushed.
                    - name: Assert package versions
                      shell: pwsh
                      run: ./eng/Assert-PackageVersions.ps1 -Path .

                    - name: PushNuget
                      run: dotnet nuget push **/*.nupkg --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET }}

                    - name: PushGithub
                      run: dotnet nuget push **/*.nupkg --no-symbols --skip-duplicate
                      env:
                        NUGET_AUTH_TOKEN: ${{ github.token }}

                """);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha);

        plan.Seeds.Should().BeEquivalentTo([
            new VersioningSeed("MintPlayer.Assertions", PackageKind.NuGet, "1.0.1-rc.4"),
            new VersioningSeed("MintPlayer.Tools", PackageKind.NuGet, "11.2.0"),
        ]);
        plan.Notes.Should().BeEmpty();
        var files = ByPath(plan);
        files.Keys.Should().BeEquivalentTo([".github/workflows/publish.yml", "Directory.Build.props", "coverage.yml",
            "src/Assertions/Assertions.csproj", "src/Tools/Tools.csproj"]);

        files["Directory.Build.props"].Should().Be("""
            <Project>
              <PropertyGroup>
                <Authors>MintPlayer</Authors>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="MintPlayer.Versioning.MSBuild" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>

            """);
        files["src/Tools/Tools.csproj"].Should().Contain("<Version>0.0.0-PLACEHOLDER</Version>").And.NotContain("11.2.0");
        files["src/Assertions/Assertions.csproj"].Should().Be("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <PackageId>MintPlayer.Assertions</PackageId>
                <Version>0.0.0-PLACEHOLDER</Version>
              </PropertyGroup>
            </Project>

            """);
        files[".github/workflows/publish.yml"].Should().Be($$$"""
            name: publish-release

            on:
              push:
                branches:
                  - 'master'

            permissions:
              contents: read
              packages: write

            jobs:
              build:
                name: publish-release
                runs-on: ubuntu-latest

                permissions:
                  packages: write
                  contents: write
                  id-token: write

                steps:
                - name: Checkout
                  uses: actions/checkout@v5

                - name: Test
                  # --no-build: the Release build is the one tested.
                  run: dotnet test --no-build --configuration Release

                - name: Version the packages
                  id: versioning
                  uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@{{{Sha}}} # versioning-v1

                - name: Pack
                  run: dotnet pack --configuration Release /p:ContinuousIntegrationBuild=true

                # The --no-build pack once shipped 99.9.9.0 (#187): check the packages before anything is pushed.
                - name: Assert package versions
                  shell: pwsh
                  run: ./eng/Assert-PackageVersions.ps1 -Path .

                - name: PushNuget
                  run: dotnet nuget push **/*.nupkg --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET }} --skip-duplicate

                - name: PushGithub
                  run: dotnet nuget push **/*.nupkg --no-symbols --skip-duplicate
                  env:
                    NUGET_AUTH_TOKEN: ${{ github.token }}

            """);
        files["coverage.yml"].Should().StartWith("# MintPlayer package versioning").And.Contain("\nversioning:\n  mode: server ");
        plan.Body.Should().Contain("| `MintPlayer.Tools` | `MintPlayer.Tools/v11.2.0` |");
    }

    [Fact]
    public async Task An_nx_repository_gets_placeholders_and_the_step_in_each_publishing_job()
    {
        var tree = new InMemoryTree()
            .Add("package.json", """{ "name": "ngb", "private": true }""")
            .Add("libs/ui/package.json", """
                {
                  "name": "@mintplayer/ng-bootstrap",
                  "version": "22.4.1",
                  "peerDependencies": {
                    "@angular/core": "^22.0.0"
                  }
                }

                """)
            .Add("libs/ui/ng-package.json", """{ "dest": "../../dist/libs/ui" }""")
            .Add("libs/snippets/package.json", """
                {
                  "name": "@mintplayer/ng-bootstrap-snippets",
                  "version": "22.0.0"
                }

                """)
            .Add("libs/snippets/project.json", "{}")
            .Add("coverage.yml", "coverage:\n  threshold: 80\n")
            .Add(".github/workflows/publish.yml", """
                name: publish

                on:
                  push:
                    branches: [ master ]

                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - uses: actions/checkout@v5
                      - run: npx nx run-many -t build
                      - uses: actions/upload-artifact@v4
                        with:
                          name: dist
                          path: dist

                  publish-libs:
                    needs: build
                    runs-on: ubuntu-latest
                    permissions:
                      contents: read
                      packages: write
                      id-token: write
                    steps:
                      - uses: actions/checkout@v5
                      - uses: actions/download-artifact@v4
                        with:
                          name: dist
                          path: dist
                      - name: Publish
                        uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
                        with:
                          folder: dist/libs

                  publish-snippets:
                    runs-on: ubuntu-latest
                    permissions:
                      contents: read
                    steps:
                      - uses: actions/checkout@v5
                      - name: Publish snippets
                        uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
                        with:
                          folder: libs/snippets

                """);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha);

        plan.Seeds.Should().BeEquivalentTo([
            new VersioningSeed("@mintplayer/ng-bootstrap", PackageKind.Npm, "22.4.1"),
            new VersioningSeed("@mintplayer/ng-bootstrap-snippets", PackageKind.Npm, "22.0.0"),
        ]);
        plan.Notes.Should().ContainSingle().Which.Should().Contain("`@mintplayer/ng-bootstrap-snippets` is not built by ng-packagr");
        var files = ByPath(plan);
        files["libs/ui/package.json"].Should().Contain("\"version\": \"0.0.0-PLACEHOLDER\",").And.Contain("\"^22.0.0\"");
        files["coverage.yml"].Should().StartWith("coverage:\n  threshold: 80\n\n# MintPlayer package versioning");
        files[".github/workflows/publish.yml"].Should().Be($$$"""
            name: publish

            on:
              push:
                branches: [ master ]

            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v5
                  - run: npx nx run-many -t build
                  - uses: actions/upload-artifact@v4
                    with:
                      name: dist
                      path: dist

              publish-libs:
                needs: build
                runs-on: ubuntu-latest
                permissions:
                  contents: write
                  packages: write
                  id-token: write
                steps:
                  - uses: actions/checkout@v5
                  - uses: actions/download-artifact@v4
                    with:
                      name: dist
                      path: dist
                  - name: Version the packages
                    id: versioning
                    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@{{{Sha}}} # versioning-v1
                  - name: Publish
                    uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
                    with:
                      folder: dist/libs

              publish-snippets:
                runs-on: ubuntu-latest
                permissions:
                  contents: write
                  id-token: write
                steps:
                  - uses: actions/checkout@v5
                  - name: Version the packages
                    id: versioning
                    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@{{{Sha}}} # versioning-v1
                  - name: Publish snippets
                    uses: MintPlayer/github-actions/publish-npm-packages@b08fbddf0b285a8633ac138b89d79ceb8cc8c888
                    with:
                      folder: libs/snippets

            """);
    }

    [Fact]
    public async Task Tagged_packages_get_no_seed_and_an_onboarded_repository_needs_nothing()
    {
        var tree = new InMemoryTree()
            .Add("Directory.Build.props", """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="MintPlayer.Versioning.MSBuild" Version="1.0.0" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """)
            .Add("src/Tools/Tools.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <PackageId>MintPlayer.Tools</PackageId>
                    <Version>0.0.0-PLACEHOLDER</Version>
                  </PropertyGroup>
                </Project>
                """)
            .Add("coverage.yml", "versioning:\n  mode: server\n");

        var plan = await VersioningOnboarding.PlanAsync(tree, new HashSet<string> { "MintPlayer.Tools" }, Sha);

        plan.Files.Should().BeEmpty();
        plan.Seeds.Should().BeEmpty();
        plan.Notes.Should().ContainSingle().Which.Should().StartWith("No push-triggered workflow");
    }

    [Fact]
    public async Task A_version_it_cannot_seed_and_a_job_without_permissions_are_left_to_the_merger()
    {
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <PackageId>MintPlayer.Tools</PackageId>
                    <Version>$(ToolsVersion)</Version>
                  </PropertyGroup>
                </Project>
                """)
            .Add(".github/workflows/manual.yml", "on: workflow_dispatch\njobs:\n  pack:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet pack\n")
            .Add(".github/workflows/publish.yml", "on: push\njobs:\n  pack:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet pack -o nupkgs\n      - run: dotnet nuget push 'nupkgs/*.nupkg' --source github\n");

        var plan = await VersioningOnboarding.PlanAsync(tree, None, null);

        plan.Seeds.Should().BeEmpty();
        plan.Notes.Should().HaveCount(3);
        plan.Notes[0].Should().Contain("`MintPlayer.Tools` is at `$(ToolsVersion)`");
        plan.Notes[1].Should().Contain("`.github/workflows/manual.yml` publishes, but not on a push");
        plan.Notes[2].Should().Contain("`.github/workflows/publish.yml` job `pack` had the repository's default token permissions");
        var files = ByPath(plan);
        files.Should().NotContainKey("src/Tools/Tools.csproj");
        files["Directory.Build.props"].Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"MintPlayer.Versioning.MSBuild\" Version=\"1.0.0\" PrivateAssets=\"all\" />\n  </ItemGroup>\n</Project>\n");
        files[".github/workflows/publish.yml"].Should().Be(
            "on: push\njobs:\n  pack:\n    runs-on: ubuntu-latest\n"
            + "    permissions:\n      packages: write\n      contents: write\n      id-token: write\n\n"
            + "    steps:\n      - name: Version the packages\n        id: versioning\n"
            + "        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1\n"
            + "      - run: dotnet pack -o nupkgs\n      - run: dotnet nuget push 'nupkgs/*.nupkg' --source github --skip-duplicate\n");
    }
}
