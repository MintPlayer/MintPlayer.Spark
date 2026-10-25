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
                <PackageReference Include="MintPlayer.Versioning.MSBuild" Version="1.0.1" PrivateAssets="all" />
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

            concurrency:
              group: ${{ github.workflow }}-${{ github.ref }}
              cancel-in-progress: true

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

                # No --no-build: the versions are decided after the tests, so pack builds again to stamp them into the
                # assemblies (--no-build also skips restore, which leaves the nuspec's dependency versions stale).
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
        files["coverage.yml"].Should().StartWith("# MintPlayer package versioning").And.Contain("\nversioning:\n  mode: server ")
            .And.Contain("\n#   prerelease: upstream ").And.EndWith("}   # free, or the framework package whose major it follows\n");
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
        plan.Notes.Should().ContainSingle().Which.Should().Contain("`@mintplayer/ng-bootstrap-snippets` has no build output the action knows");
        var files = ByPath(plan);
        files["libs/ui/package.json"].Should().Contain("\"version\": \"0.0.0-PLACEHOLDER\",").And.Contain("\"^22.0.0\"");
        files["coverage.yml"].Should().StartWith("coverage:\n  threshold: 80\n\n# MintPlayer package versioning");
        files[".github/workflows/publish.yml"].Should().Be($$$"""
            name: publish

            on:
              push:
                branches: [ master ]

            concurrency:
              group: ${{ github.workflow }}-${{ github.ref }}
              cancel-in-progress: true

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
    public async Task A_js_devtools_npm_publish_step_gets_the_versioning_step_in_front()
    {
        // mintplayer-ng-video-player#101: its 40 JS-DevTools/npm-publish steps were not seen as publishing, so the workflow was left alone.
        var tree = new InMemoryTree()
            .Add("libs/player/package.json", "{\n  \"name\": \"@mintplayer/video-player\",\n  \"version\": \"20.1.0\"\n}\n")
            .Add("libs/player/project.json", """{ "targets": { "build": { "options": { "outputPath": "{workspaceRoot}/dist/{projectRoot}" } } } }""")
            .Add(".github/workflows/publish-master.yml", """
                name: npm-publish

                on:
                  push:
                    branches: [ master ]

                jobs:
                  build:
                    runs-on: ubuntu-latest
                    permissions:
                      contents: read
                      id-token: write
                    steps:
                    - run: npm run build
                    - run: npx nx run-many --target=test --all

                    - name: Push to NPM
                      uses: JS-DevTools/npm-publish@v4
                      with:
                        package: 'dist/libs/player/package.json'

                """);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha);

        plan.Notes.Should().BeEmpty();
        ByPath(plan)[".github/workflows/publish-master.yml"].Should().Contain($$"""
                  contents: write
                  id-token: write
                steps:
                - run: npm run build
                - run: npx nx run-many --target=test --all

                - name: Version the packages
                  id: versioning
                  uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@{{Sha}} # versioning-v1

                - name: Push to NPM
                  uses: JS-DevTools/npm-publish@v4
            """);
    }

    [Fact]
    public async Task A_file_with_a_byte_order_mark_keeps_it()
    {
        // MintPlayer.AspNetCore.SpaServices#91: the onboarding wrote five csproj files back without their BOM.
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", "﻿<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
                + "    <PackageId>MintPlayer.Tools</PackageId>\n    <Version>11.2.0</Version>\n  </PropertyGroup>\n</Project>\n")
            .Add("Directory.Build.props", "<Project>\n</Project>\n");

        var files = ByPath(await VersioningOnboarding.PlanAsync(tree, None, Sha));

        files["src/Tools/Tools.csproj"].Should().Be("﻿<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
            + "    <PackageId>MintPlayer.Tools</PackageId>\n    <Version>0.0.0-PLACEHOLDER</Version>\n  </PropertyGroup>\n</Project>\n");
        files["Directory.Build.props"].Should().StartWith("<Project>");
    }

    [Fact]
    public async Task MinVer_goes_from_the_repository_build_but_stays_in_props_a_package_ships()
    {
        // MintPlayer.Dotnet.Tools#198: the onboarding took MinVer out of the Vidyano SDK's props, which consumers import.
        const string sdkProps = "<Project>\n  <ItemGroup>\n    <GlobalPackageReference Include=\"MinVer\" Version=\"6.0.0\" PrivateAssets=\"all\" />\n  </ItemGroup>\n</Project>\n";
        var tree = new InMemoryTree()
            .Add("Directory.Packages.props", "<Project>\n  <ItemGroup>\n    <GlobalPackageReference Include=\"MinVer\" Version=\"6.0.0\" PrivateAssets=\"all\" />\n    <PackageVersion Include=\"Xunit\" Version=\"2.9.0\" />\n  </ItemGroup>\n</Project>\n")
            .Add("Sdks/Acme.Sdk/Acme.Sdk.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
                + "    <PackageId>Acme.Sdk</PackageId>\n    <Version>11.0.0</Version>\n  </PropertyGroup>\n</Project>\n")
            .Add("Sdks/Acme.Sdk/Sdk/Acme.props", sdkProps);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha);
        var files = ByPath(plan);

        files.Should().NotContainKey("Sdks/Acme.Sdk/Sdk/Acme.props");
        files["Directory.Packages.props"].Should().NotContain("MinVer").And.Contain("Xunit");
        plan.Notes.Should().Contain(n => n.StartsWith("`Sdks/Acme.Sdk/Sdk/Acme.props` mentions MinVer and was left alone"));
    }

    [Fact]
    public async Task Tagged_packages_get_no_seed_and_an_onboarded_repository_needs_nothing()
    {
        var tree = new InMemoryTree()
            .Add("Directory.Build.props", """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="MintPlayer.Versioning.MSBuild" Version="1.0.1" PrivateAssets="all" />
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
        files["Directory.Build.props"].Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"MintPlayer.Versioning.MSBuild\" Version=\"1.0.1\" PrivateAssets=\"all\" />\n  </ItemGroup>\n</Project>\n");
        files[".github/workflows/publish.yml"].Should().Be(
            "on: push\n"
            + "concurrency:\n  group: ${{ github.workflow }}-${{ github.ref }}\n  cancel-in-progress: true\n\n"
            + "jobs:\n  pack:\n    runs-on: ubuntu-latest\n"
            + "    permissions:\n      packages: write\n      contents: write\n      id-token: write\n\n"
            + "    steps:\n      - name: Version the packages\n        id: versioning\n"
            + "        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1\n"
            + "      # No --no-build: the versions are decided after the tests, so pack builds again to stamp them into the\n"
            + "      # assemblies (--no-build also skips restore, which leaves the nuspec's dependency versions stale).\n"
            + "      - run: dotnet pack -o nupkgs\n      - run: dotnet nuget push 'nupkgs/*.nupkg' --source github --skip-duplicate\n");
    }

    [Fact]
    public async Task Tests_after_the_tags_are_reported_and_an_own_concurrency_is_kept()
    {
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <PackageId>MintPlayer.Tools</PackageId>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n")
            .Add(".github/workflows/publish.yml", "on: push\nconcurrency: release\njobs:\n"
                + "  unit:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n"
                + "  lint:\n    runs-on: ubuntu-latest\n    steps:\n      - run: npx nx affected -t lint test\n"
                + "  build:\n    needs: lint\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet build\n"
                + "  pack:\n    needs: [build]\n    runs-on: ubuntu-latest\n    permissions:\n      contents: write\n      id-token: write\n"
                + "    steps:\n      - run: dotnet pack\n      - run: dotnet test --no-build\n");

        var plan = await VersioningOnboarding.PlanAsync(tree, None, null);

        plan.Notes.Should().HaveCount(2);
        plan.Notes[0].Should().Contain("job `pack` runs tests after it packs");
        plan.Notes[1].Should().Contain("job `pack` does not wait for job `unit`");
        var workflow = ByPath(plan)[".github/workflows/publish.yml"];
        workflow.Should().StartWith("on: push\nconcurrency: release\njobs:\n");
        workflow.Should().NotContain("cancel-in-progress");
    }

    private const string Feed = "https://nuget.pkg.github.com/MintPlayer/index.json";

    private static InMemoryTree GitHubPackagesRepository(string sourceUrl) => new InMemoryTree()
        .Add("src/Tools/Tools.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <PackageId>MintPlayer.Tools</PackageId>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n")
        .Add(".github/workflows/build.yml", $"on: pull_request\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n"
            + $"      - uses: actions/setup-dotnet@v5\n        with:\n          source-url: {sourceUrl}\n        env:\n          NUGET_AUTH_TOKEN: ${{{{ github.token }}}}\n"
            + "      - run: dotnet build\n");

    [Fact]
    public async Task A_github_packages_restore_gets_a_nuget_config_mapping_the_package_to_nuget_org()
    {
        var plan = await VersioningOnboarding.PlanAsync(GitHubPackagesRepository(Feed), None, Sha);

        plan.Notes.Should().ContainSingle().Which.Should().StartWith("No push-triggered");
        var files = ByPath(plan);
        files.Should().NotContainKey(".github/workflows/build.yml");
        // The raw string takes the checkout's line endings; the new file is LF.
        files["nuget.config"].ReplaceLineEndings().Should().Be($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="github" value="{Feed}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                  <package pattern="MintPlayer.Versioning.MSBuild" />
                </packageSource>
                <packageSource key="github">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>

            """);
    }

    [Fact]
    public async Task A_nuget_config_without_a_mapping_keeps_its_sources_and_gets_one()
    {
        var tree = GitHubPackagesRepository(Feed).Add("NuGet.Config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
                    <add key="MintPlayer" value="https://NUGET.pkg.github.com/mintplayer/index.json/" />
                </packageSources>
            </configuration>

            """);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha);

        ByPath(plan)["NuGet.Config"].Should().Be("""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
                    <add key="MintPlayer" value="https://NUGET.pkg.github.com/mintplayer/index.json/" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="nuget">
                        <package pattern="*" />
                        <package pattern="MintPlayer.Versioning.MSBuild" />
                    </packageSource>
                    <packageSource key="MintPlayer">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>

            """);
    }

    [Fact]
    public async Task A_mapping_that_sends_the_prefix_to_github_gets_the_exact_id_under_nuget_org()
    {
        var config = $"""
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="github" value="{Feed}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="github">
                  <package pattern="MintPlayer.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>

            """;
        var notes = new List<string>();

        var edited = VersioningOnboarding.NuGetConfig("nuget.config", config, [Feed], notes);

        notes.Should().BeEmpty();
        edited.Should().Be(config.Replace("""
                  <package pattern="*" />
                </packageSource>
                <packageSource key="github">
            """, """
                  <package pattern="*" />
                  <package pattern="MintPlayer.Versioning.MSBuild" />
                </packageSource>
                <packageSource key="github">
            """));
        VersioningOnboarding.NuGetConfig("nuget.config", edited, [Feed], notes).Should().BeNull();
    }

    [Fact]
    public async Task A_source_url_naming_the_owner_through_the_context_resolves_to_the_repository_owner()
    {
        // The shape of MintPlayer.AspNetCore.Tools and SpaServices.
        var tree = GitHubPackagesRepository("https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json").Add("nuget.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha, owner: "MintPlayer");

        ByPath(plan)["nuget.config"].Should().Be($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                <add key="github" value="{Feed}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                  <package pattern="MintPlayer.Versioning.MSBuild" />
                </packageSource>
                <packageSource key="github">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
    }

    [Fact]
    public async Task A_source_url_from_an_expression_is_left_to_the_merger()
    {
        var plan = await VersioningOnboarding.PlanAsync(GitHubPackagesRepository("https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json"), None, Sha);

        plan.Notes.Where(n => !n.StartsWith("No push-triggered", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().Contain("sets setup-dotnet's `source-url` from an expression");
        ByPath(plan).Should().NotContainKey("nuget.config");
    }

    [Fact]
    public async Task The_reference_takes_the_version_read_from_nuget_org()
    {
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <PackageId>MintPlayer.Tools</PackageId>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n");

        var plan = await VersioningOnboarding.PlanAsync(tree, None, Sha, "1.4.0");

        ByPath(plan)["Directory.Build.props"].Should().Contain("<PackageReference Include=\"MintPlayer.Versioning.MSBuild\" Version=\"1.4.0\" PrivateAssets=\"all\" />");
    }

    [Theory]
    [InlineData("""{"versions":["1.0.0","1.0.1","1.10.0-preview.1","1.2.0"]}""", "1.2.0")]
    [InlineData("""{"versions":["2.0.0-rc.1"]}""", null)]
    [InlineData("""{"versions":[]}""", null)]
    [InlineData("""{}""", null)]
    public void Latest_stable_skips_prereleases_and_compares_numerically(string json, string? expected)
        => VersioningOnboarding.LatestStable(json).Should().Be(expected);
}
