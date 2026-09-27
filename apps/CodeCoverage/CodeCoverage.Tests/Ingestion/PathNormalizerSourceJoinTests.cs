using Xunit;
using CodeCoverage.Ingestion;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// A Cobertura filename is relative to the report's <c>&lt;source&gt;</c>. Vitest writes
/// exactly that shape (source = the package directory, filename = package-relative), and
/// two packages that share a relative path are then indistinguishable by suffix alone —
/// on master ddcd8907 both <c>translate-key.pipe.ts</c> files dropped out of the badge.
/// </summary>
public class PathNormalizerSourceJoinTests
{
    private const string RootDir = "/home/runner/work/MintPlayer.Spark/MintPlayer.Spark";

    private static readonly string[] FileList =
    [
        "libs/node_packages/ng-spark/pipes/src/translate-key.pipe.ts",
        "libs/node_packages/ng-spark-auth/pipes/src/translate-key.pipe.ts",
        "libs/node_packages/ng-spark/src/test-utils.ts",
        "libs/node_packages/ng-spark-auth/src/test-utils.ts",
        "src/test-utils.ts",
    ];

    [Theory]
    [InlineData("libs/node_packages/ng-spark")]
    [InlineData("libs/node_packages/ng-spark-auth")]
    public void Joins_an_absolute_source_under_the_workspace_with_a_shared_tail(string package)
    {
        var normalizer = new PathNormalizer(RootDir, [$"{RootDir}/{package}"], FileList);

        var (path, matched) = normalizer.Normalize("pipes/src/translate-key.pipe.ts");

        path.Should().Be($"{package}/pipes/src/translate-key.pipe.ts");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Without_the_source_the_shared_tail_stays_ambiguous()
    {
        var normalizer = new PathNormalizer(RootDir, [], FileList);

        var (_, matched) = normalizer.Normalize("pipes/src/translate-key.pipe.ts");

        matched.Should().BeFalse();
    }

    [Fact]
    public void Joins_a_windows_source_with_a_backslash_filename()
    {
        var normalizer = new PathNormalizer(
            @"D:\a\MintPlayer.Spark\MintPlayer.Spark",
            [@"D:\a\MintPlayer.Spark\MintPlayer.Spark\libs\node_packages\ng-spark-auth\"],
            FileList);

        var (path, matched) = normalizer.Normalize(@"pipes\src\translate-key.pipe.ts");

        path.Should().Be("libs/node_packages/ng-spark-auth/pipes/src/translate-key.pipe.ts");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Joins_a_relative_source()
    {
        var normalizer = new PathNormalizer(null, ["libs/node_packages/ng-spark"], FileList);

        var (path, matched) = normalizer.Normalize("pipes/src/translate-key.pipe.ts");

        path.Should().Be("libs/node_packages/ng-spark/pipes/src/translate-key.pipe.ts");
        matched.Should().BeTrue();
    }

    [Fact]
    public void The_source_relative_file_wins_over_a_same_named_file_at_the_repo_root()
    {
        var normalizer = new PathNormalizer(RootDir, [$"{RootDir}/libs/node_packages/ng-spark"], FileList);

        var (path, matched) = normalizer.Normalize("src/test-utils.ts");

        path.Should().Be("libs/node_packages/ng-spark/src/test-utils.ts");
        matched.Should().BeTrue();
    }

    [Fact]
    public void Two_sources_naming_two_different_files_stay_unmatched()
    {
        var normalizer = new PathNormalizer(
            RootDir,
            [$"{RootDir}/libs/node_packages/ng-spark", $"{RootDir}/libs/node_packages/ng-spark-auth"],
            FileList);

        var (_, matched) = normalizer.Normalize("pipes/src/translate-key.pipe.ts");

        matched.Should().BeFalse();
    }

    [Fact]
    public void Falls_back_to_the_bare_path_when_the_join_names_nothing()
    {
        var normalizer = new PathNormalizer(RootDir, [$"{RootDir}/libs/node_packages/ng-spark"], FileList);

        // Already repo-relative (the cobertura `projectRoot` shape): the join would name
        // libs/node_packages/ng-spark/libs/..., which is not tracked.
        var (path, matched) = normalizer.Normalize("libs/node_packages/ng-spark-auth/src/test-utils.ts");

        path.Should().Be("libs/node_packages/ng-spark-auth/src/test-utils.ts");
        matched.Should().BeTrue();
    }

    [Fact]
    public void A_source_outside_the_workspace_is_not_joined()
    {
        var normalizer = new PathNormalizer(RootDir, ["/elsewhere/libs/node_packages/ng-spark"], FileList);

        var (_, matched) = normalizer.Normalize("pipes/src/translate-key.pipe.ts");

        matched.Should().BeFalse();
    }
}
