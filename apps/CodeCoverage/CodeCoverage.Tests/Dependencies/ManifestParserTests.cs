using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using Xunit;

namespace CodeCoverage.Tests.Dependencies;

/// <summary>
/// The manifest parsers behind the repository dependency graph (dependency-updates PRD §6), run over
/// real manifests: this repository's own, and well-known public projects' for the ecosystems it does
/// not use (Flask and Poetry for Python, Laravel for Composer, linuxserver.io for a ghcr.io base
/// image). The fixtures carry a <c>.txt</c> suffix so Nx and MSBuild never mistake them for projects.
/// </summary>
public class ManifestParserTests
{
    private static ManifestParseResult Parse(string fixture, string repositoryPath)
        => ManifestFiles.Parse(repositoryPath, Fixture.Read($"Manifests/{fixture}"));

    private static ManifestDependency Consumed(ManifestParseResult result, string name)
        => result.Consumes.Should().ContainSingle(d => d.Name == name).Which;

    // ── Classification ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("package.json", EManifestKind.NpmPackageJson)]
    [InlineData("libs/ng-spark/package.json", EManifestKind.NpmPackageJson)]
    [InlineData("src/App/App.csproj", EManifestKind.MsBuildProject)]
    [InlineData("src/Lib/Lib.FSPROJ", EManifestKind.MsBuildProject)]
    [InlineData("Directory.Packages.props", EManifestKind.MsBuildDirectoryFile)]
    [InlineData("src/Directory.Build.targets", EManifestKind.MsBuildDirectoryFile)]
    [InlineData("requirements.txt", EManifestKind.PipRequirements)]
    [InlineData("requirements-dev.txt", EManifestKind.PipRequirements)]
    [InlineData("py/pyproject.toml", EManifestKind.PyProject)]
    [InlineData("composer.json", EManifestKind.ComposerJson)]
    [InlineData(".github/workflows/ci.yml", EManifestKind.ActionsWorkflow)]
    [InlineData(".github/workflows/release.yaml", EManifestKind.ActionsWorkflow)]
    [InlineData("tools/my-action/action.yml", EManifestKind.ActionsWorkflow)]
    [InlineData("Dockerfile", EManifestKind.Dockerfile)]
    [InlineData("apps/api/Dockerfile.prod", EManifestKind.Dockerfile)]
    [InlineData("docker-compose.override.yml", EManifestKind.Compose)]
    [InlineData("deploy/compose.yaml", EManifestKind.Compose)]
    [InlineData("README.md", EManifestKind.None)]
    [InlineData("package-lock.json", EManifestKind.None)]
    [InlineData(".github/workflows/nested/ci.yml", EManifestKind.None)]
    [InlineData("config/settings.yml", EManifestKind.None)]
    public void Paths_are_classified_by_file_name(string path, EManifestKind expected)
        => ManifestFiles.Classify(path).Should().Be(expected);

    [Theory]
    [InlineData("node_modules/left-pad/package.json")]
    [InlineData("web/node_modules/x/package.json")]
    [InlineData("vendor/laravel/framework/composer.json")]
    [InlineData("src/App/bin/Debug/App.csproj")]
    [InlineData("src/App/obj/project.csproj")]
    [InlineData("dist/package.json")]
    [InlineData(".git/package.json")]
    public void Installed_vendored_and_built_paths_are_skipped(string path)
        => ManifestFiles.IsManifest(path).Should().BeFalse();

    [Fact]
    public void A_file_named_like_a_skipped_directory_is_still_read()
        => ManifestFiles.IsManifest("tools/dist.csproj").Should().BeTrue();

    // ── npm ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_published_npm_package_produces_its_name_and_consumes_its_peers()
    {
        var result = Parse("ng-spark.package.json.txt", "libs/node_packages/ng-spark/package.json");

        result.Error.Should().BeNull();
        var produced = result.Produces.Should().ContainSingle().Which;
        produced.Ecosystem.Should().Be("npm");
        produced.Name.Should().Be("@mintplayer/ng-spark");
        produced.Path.Should().Be("libs/node_packages/ng-spark/package.json");

        var core = Consumed(result, "@angular/core");
        core.Constraint.Should().Be("^22.0.0");
        core.Dev.Should().BeFalse();
        Consumed(result, "@mintplayer/ng-bootstrap").Constraint.Should().Be("^22.22.0");
    }

    [Fact]
    public void A_private_package_produces_nothing_but_still_consumes_with_dev_flags()
    {
        var result = Parse("coverage-action.package.json.txt", "apps/CodeCoverage/action/package.json");

        result.Produces.Should().BeEmpty();
        Consumed(result, "@actions/core").Dev.Should().BeFalse();
        Consumed(result, "vitest").Dev.Should().BeTrue();
        result.Consumes.Should().OnlyContain(d => d.Ecosystem == "npm" && d.Path == "apps/CodeCoverage/action/package.json");
    }

    [Fact]
    public void The_private_workspace_root_produces_nothing()
        => Parse("root.package.json.txt", "package.json").Produces.Should().BeEmpty();

    [Fact]
    public void Workspace_file_and_link_specs_are_skipped_and_an_alias_consumes_the_real_package()
    {
        const string json = """
            {
              "name": "@acme/app",
              "dependencies": {
                "@acme/sibling": "workspace:*",
                "local": "file:../local",
                "linked": "link:../linked",
                "lodash4": "npm:lodash@^4.17.21",
                "react": "^19.0.0"
              }
            }
            """;

        var result = ManifestFiles.Parse("package.json", json);

        result.Consumes.Select(d => d.Name).Should().Equal("lodash", "react");
        Consumed(result, "lodash").Constraint.Should().Be("^4.17.21");
    }

    [Fact]
    public void Malformed_json_yields_an_error_and_nothing_else()
    {
        var result = ManifestFiles.Parse("package.json", "{ \"name\": ");

        result.Error.Should().NotBeNull();
        result.Produces.Should().BeEmpty();
        result.Consumes.Should().BeEmpty();
    }

    // ── NuGet ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_packable_web_sdk_project_produces_its_package_id()
    {
        var result = Parse("MintPlayer.Spark.csproj.txt", "libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj");

        result.Error.Should().BeNull();
        var produced = result.Produces.Should().ContainSingle().Which;
        produced.Ecosystem.Should().Be("nuget");
        produced.Name.Should().Be("MintPlayer.Spark");

        Consumed(result, "RavenDB.Client").Constraint.Should().Be("7.2.6");
        Consumed(result, "RavenDB.Client").Dev.Should().BeFalse();
        // <PrivateAssets>all</PrivateAssets> as a child element: a build-time-only reference.
        Consumed(result, "MintPlayer.SourceGenerators").Dev.Should().BeTrue();
    }

    [Fact]
    public void A_web_application_is_not_packable()
    {
        var result = Parse("CodeCoverage.csproj.txt", "apps/CodeCoverage/CodeCoverage/CodeCoverage.csproj");

        result.Produces.Should().BeEmpty();
        Consumed(result, "YamlDotNet").Constraint.Should().Be("18.1.0");
    }

    [Fact]
    public void A_test_project_produces_nothing_and_every_reference_is_dev()
    {
        var result = Parse("CodeCoverage.Tests.csproj.txt", "apps/CodeCoverage/CodeCoverage.Tests/CodeCoverage.Tests.csproj");

        result.Produces.Should().BeEmpty();
        result.Consumes.Should().NotBeEmpty();
        result.Consumes.Should().OnlyContain(d => d.Dev);
        Consumed(result, "xunit").Constraint.Should().Be("2.9.3");
    }

    [Theory]
    [InlineData("<PackageId>Acme.Widgets</PackageId><AssemblyName>Acme.Widgets.Core</AssemblyName>", "Acme.Widgets")]
    [InlineData("<AssemblyName>Acme.Widgets.Core</AssemblyName>", "Acme.Widgets.Core")]
    [InlineData("<PackageId>$(RootNamespace).Widgets</PackageId>", "Widgets")]
    [InlineData("", "Widgets")]
    public void The_produced_id_falls_back_from_package_id_to_assembly_name_to_file_name(string properties, string expected)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework>{properties}</PropertyGroup>
            </Project>
            """;

        ManifestFiles.Parse("src/Widgets/Widgets.csproj", csproj).Produces.Should().ContainSingle().Which.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("<PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>")]
    [InlineData("<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>")]
    [InlineData("<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" /></ItemGroup>")]
    public void Unpackable_projects_produce_nothing(string body)
        => ManifestFiles.Parse("Widgets.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{body}</Project>").Produces.Should().BeEmpty();

    [Fact]
    public void Directory_files_only_consume_and_ignore_msbuild_expressions()
    {
        const string props = """
            <Project>
              <PropertyGroup><PackageId>Never.Produced</PackageId></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" PrivateAssets="All" />
                <PackageReference Include="$(SharedPackage)" Version="1.0.0" />
                <PackageReference Update="Newtonsoft.Json" Version="13.0.3" />
                <GlobalPackageReference Include="Nerdbank.GitVersioning" Version="3.7.115" />
              </ItemGroup>
            </Project>
            """;

        var result = ManifestFiles.Parse("Directory.Build.props", props);

        result.Produces.Should().BeEmpty();
        result.Consumes.Select(d => d.Name).Should().Equal("Microsoft.SourceLink.GitHub", "Nerdbank.GitVersioning");
        Consumed(result, "Microsoft.SourceLink.GitHub").Dev.Should().BeTrue();
    }

    [Fact]
    public void A_byte_order_mark_and_a_dtd_are_handled_without_throwing()
    {
        ManifestFiles.Parse("A.csproj", "﻿<Project Sdk=\"Microsoft.NET.Sdk\" />").Produces.Should().ContainSingle().Which.Name.Should().Be("A");

        var withDtd = ManifestFiles.Parse("B.csproj", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Project>&e;</Project>");
        withDtd.Error.Should().NotBeNull();
        withDtd.Consumes.Should().BeEmpty();
    }

    // ── pip ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pep621_pyproject_produces_its_normalized_name_and_consumes_every_group()
    {
        var result = Parse("flask.pyproject.toml.txt", "pyproject.toml");

        result.Error.Should().BeNull();
        result.Produces.Should().ContainSingle().Which.Name.Should().Be("flask");

        var blinker = Consumed(result, "blinker");
        blinker.Constraint.Should().Be(">=1.9.0");
        blinker.Dev.Should().BeFalse();
        // [project.optional-dependencies] dotenv: a feature extra, installed by consumers. It is also
        // in the dev groups, which records it a second time as dev.
        result.Consumes.Any(d => d.Name == "python-dotenv" && !d.Dev).Should().BeTrue();
        result.Consumes.Any(d => d.Name == "python-dotenv" && d.Dev).Should().BeTrue();
        // [dependency-groups]: development only by definition.
        Consumed(result, "ruff").Dev.Should().BeTrue();
    }

    [Fact]
    public void Poetry_groups_are_dev_and_markers_are_not_constraints()
    {
        var result = Parse("poetry.pyproject.toml.txt", "pyproject.toml");

        result.Produces.Should().ContainSingle().Which.Name.Should().Be("poetry");
        Consumed(result, "cachecontrol").Constraint.Should().Be(">=0.14.0,<0.15.0");
        Consumed(result, "tomli").Constraint.Should().Be(">=2.0.1,<3.0.0");
        Consumed(result, "pre-commit").Dev.Should().BeTrue();
        var xdist = Consumed(result, "pytest-xdist");
        xdist.Constraint.Should().Be(">=3.1");
        xdist.Dev.Should().BeTrue();
    }

    [Fact]
    public void A_requirements_file_skips_options_and_reads_names_and_ranges()
    {
        var result = Parse("requests.requirements-dev.txt", "requirements-dev.txt");

        result.Produces.Should().BeEmpty();
        result.Consumes.Select(d => d.Name).Should().Equal("pytest", "pytest-cov", "pytest-httpbin", "httpbin", "trustme", "wheel");
        Consumed(result, "pytest").Constraint.Should().Be(">=2.8.0,<10");
        Consumed(result, "trustme").Constraint.Should().BeNull();
        result.Consumes.Should().OnlyContain(d => d.Dev, "the file is named for development");
    }

    [Theory]
    [InlineData("Django_REST.framework>=3", "django-rest-framework", ">=3")]
    [InlineData("requests[security] ~= 2.31 ; python_version >= '3.8'", "requests", "~= 2.31")]
    [InlineData("mylib @ https://example.com/mylib-1.0.tar.gz", "mylib", null)]
    public void Requirement_names_are_normalized_per_pep_503(string line, string name, string? constraint)
    {
        var result = ManifestFiles.Parse("requirements.txt", line + "\n# a comment\n-r base.txt\ngit+https://github.com/a/b.git\n");

        var dependency = result.Consumes.Should().ContainSingle().Which;
        dependency.Name.Should().Be(name);
        dependency.Constraint.Should().Be(constraint);
    }

    [Fact]
    public void Malformed_toml_yields_an_error()
        => ManifestFiles.Parse("pyproject.toml", "[project\nname = ").Error.Should().NotBeNull();

    // ── Composer ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_composer_library_produces_its_name_and_skips_platform_requirements()
    {
        var result = Parse("laravel-framework.composer.json.txt", "composer.json");

        result.Produces.Should().ContainSingle().Which.Name.Should().Be("laravel/framework");
        Consumed(result, "symfony/console").Constraint.Should().Be("^7.2.0");
        Consumed(result, "phpunit/phpunit").Dev.Should().BeTrue();
        result.Consumes.Should().OnlyContain(d => d.Name.Contains('/'), "php and ext-* are platform requirements");
    }

    [Fact]
    public void A_composer_project_produces_nothing()
    {
        var result = Parse("laravel-app.composer.json.txt", "composer.json");

        result.Produces.Should().BeEmpty();
        Consumed(result, "laravel/framework").Dev.Should().BeFalse();
    }

    // ── GitHub Actions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_workflow_consumes_its_actions_by_repository_and_skips_local_ones()
    {
        var result = Parse("pull-request.workflow.yml.txt", ".github/workflows/pull-request.yml");

        result.Consumes.Should().OnlyContain(d => d.Ecosystem == "actions");
        Consumed(result, "actions/checkout").Constraint.Should().Be("v7");
        Consumed(result, "nrwl/nx-set-shas").Constraint.Should().Be("v5");
        // `MintPlayer/github-actions/compile-ts-action@main`: a sub-path action consumes its repository.
        Consumed(result, "mintplayer/github-actions").Constraint.Should().Be("main");
        result.Consumes.Should().OnlyContain(d => !d.Name.StartsWith("./") && !d.Name.StartsWith("apps/"));
    }

    [Fact]
    public void Quoted_list_item_and_container_uses_are_handled()
    {
        const string yaml = """
            jobs:
              build:
                steps:
                  - uses: "Acme/Setup-Thing@v2" # pinned
                  - name: container
                    uses: docker://alpine:3.20
              call:
                uses: acme/workflows/.github/workflows/release.yml@v1
            """;

        var result = ManifestFiles.Parse(".github/workflows/ci.yml", yaml);

        result.Consumes.Select(d => d.Name).Should().Equal("acme/setup-thing", "acme/workflows");
    }

    // ── Docker ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_dockerfile_consumes_only_ghcr_images_without_tag()
    {
        var result = Parse("linuxserver-nginx.Dockerfile.txt", "Dockerfile");

        var image = result.Consumes.Should().ContainSingle().Which;
        image.Ecosystem.Should().Be("docker");
        image.Name.Should().Be("ghcr.io/linuxserver/baseimage-alpine-nginx");
        image.Constraint.Should().Be("3.24");
    }

    [Fact]
    public void A_dockerfile_on_microsoft_images_consumes_nothing()
        => Parse("codecoverage.Dockerfile.txt", "apps/CodeCoverage/CodeCoverage/Dockerfile").Consumes.Should().BeEmpty();

    [Fact]
    public void A_compose_file_consumes_its_ghcr_images()
    {
        var result = Parse("codecoverage.docker-compose.yml.txt", "apps/CodeCoverage/docker-compose.yml");

        var image = result.Consumes.Should().ContainSingle().Which;
        image.Name.Should().Be("ghcr.io/mintplayer/codecoverage");
        image.Constraint.Should().Be("master");
    }

    [Theory]
    [InlineData("FROM --platform=$BUILDPLATFORM ghcr.io/Acme/Base:1.2@sha256:abc AS build", "ghcr.io/acme/base")]
    [InlineData("from ghcr.io/acme/tools/node", "ghcr.io/acme/tools/node")]
    public void Image_references_are_normalized(string line, string expected)
        => ManifestFiles.Parse("Dockerfile", line).Consumes.Should().ContainSingle().Which.Name.Should().Be(expected);

    [Theory]
    [InlineData("FROM ${REGISTRY}/acme/base:1")]
    [InlineData("FROM ghcr.io/acme")]
    [InlineData("FROM build AS final")]
    public void Unresolvable_or_foreign_images_are_skipped(string line)
        => ManifestFiles.Parse("Dockerfile", line).Consumes.Should().BeEmpty();

    [Fact]
    public void Implicit_products_are_named_after_the_repository()
    {
        ActionsManifestParser.ProducedName("MintPlayer/MintPlayer.Spark").Should().Be("mintplayer/mintplayer.spark");
        DockerManifestParser.ProducedName("MintPlayer/CodeCoverage").Should().Be("ghcr.io/mintplayer/codecoverage");
    }

    // ── Push trigger filter ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_push_scans_when_a_manifest_changed_or_the_commit_list_may_be_truncated()
    {
        ManifestScanTriggers.PushTouchesManifests(1, ["src/a.ts", "web/package.json"]).Should().BeTrue();
        ManifestScanTriggers.PushTouchesManifests(1, ["src/a.ts", "node_modules/x/package.json"]).Should().BeFalse();
        ManifestScanTriggers.PushTouchesManifests(20, []).Should().BeTrue();
        ManifestScanTriggers.PushTouchesManifests(19, ["README.md"]).Should().BeFalse();
    }

    [Fact]
    public void Manifest_ids_derive_from_repository_ids()
    {
        RepositoryManifest.ForRepository("Repositories/github/123").Should().Be("RepositoryManifests/github/123");
        RepositoryManifest.DocumentId(CodeCoverage.Forge.EForgeProvider.GitHub, 123).Should().Be("RepositoryManifests/github/123");
    }
}
