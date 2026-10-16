using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// Gap B (docs/code-coverage/dependency-updates-PRD.md §13.11.4, §13.11.3 #5): the bot's own scan of the lines an apply
/// patch adds to a lockfile, on diffs written by real <c>git diff</c>. The real S13 lockfile changes of every ecosystem
/// pass in <see cref="DependencyPatchProfileTests.Every_pure_spike_patch_passes_against_its_plan"/>.
/// </summary>
public class LockfileScannerTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private const string NpmLock =
        "{\n" +
        "  \"packages\": {\n" +
        "    \"node_modules/express\": {\n" +
        "      \"version\": \"4.18.2\",\n" +
        "      \"resolved\": \"https://registry.npmjs.org/express/-/express-4.18.2.tgz\",\n" +
        "      \"integrity\": \"sha512-old\"\n" +
        "    },\n" +
        "    \"node_modules/internal\": {\n" +
        "      \"version\": \"1.0.0\",\n" +
        "      \"resolved\": \"https://npm.pkg.github.com/internal/-/internal-1.0.0.tgz\",\n" +
        "      \"integrity\": \"sha512-internal\"\n" +
        "    }\n" +
        "  }\n" +
        "}\n";

    private LockfileScan Scan(string path, string before, string after, LockfileScanOptions? options = null)
    {
        var from = repo.Commit((path, before));
        var to = repo.Change(from, (path, after));
        var files = new List<PatchFile>();
        PatchValidator.Parse(repo.Diff(from, to), _ => null, 1024 * 1024, files).Accepted.Should().BeTrue();
        return LockfileScanner.Scan(files.Single(), before, options ?? LockfileScanOptions.Default);
    }

    private static string Express(string resolved, string integrity = "\"integrity\": \"sha512-new\"")
        => NpmLock.Replace("\"version\": \"4.18.2\"", "\"version\": \"4.19.2\"", StringComparison.Ordinal)
            .Replace("\"resolved\": \"https://registry.npmjs.org/express/-/express-4.18.2.tgz\"", $"\"resolved\": \"{resolved}\"", StringComparison.Ordinal)
            .Replace("\"integrity\": \"sha512-old\"", integrity, StringComparison.Ordinal);

    [Fact]
    public void A_registry_bump_with_its_integrity_passes()
    {
        var scan = Scan("package-lock.json", NpmLock, Express("https://registry.npmjs.org/express/-/express-4.19.2.tgz"));

        scan.Refused.Should().BeFalse();
        scan.NewPackages.Should().BeEmpty();
    }

    [Fact]
    public void A_host_the_base_lockfile_already_names_is_allowed()
    {
        var scan = Scan("package-lock.json", NpmLock, Express("https://npm.pkg.github.com/express/-/express-4.19.2.tgz"));

        scan.Refused.Should().BeFalse();
    }

    // §13.11.3 #5: an attacker host; git+, github:, file:, link:, http: sources; credentials in a URL.
    [Theory]
    [InlineData("https://evil.example/express/-/express-4.19.2.tgz")]
    [InlineData("https://registry.npmjs.org.evil.example/express-4.19.2.tgz")]
    [InlineData("git+https://github.com/evil/express.git#abc")]
    [InlineData("git+ssh://git@github.com/evil/express.git")]
    [InlineData("github:evil/express")]
    [InlineData("file:../evil.tgz")]
    [InlineData("link:../evil")]
    [InlineData("http://registry.npmjs.org/express/-/express-4.19.2.tgz")]
    [InlineData("https://user:token@registry.npmjs.org/express/-/express-4.19.2.tgz")]
    public void A_source_that_is_not_an_allowed_registry_is_refused(string resolved)
    {
        var scan = Scan("package-lock.json", NpmLock, Express(resolved));

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileSource);
    }

    [Fact]
    public void An_allowed_host_can_be_configured()
    {
        var scan = Scan("package-lock.json", NpmLock, Express("https://registry.example.com/express-4.19.2.tgz"),
            LockfileScanOptions.Default with { AllowedHosts = [.. LockfileScanOptions.PublicRegistries, "registry.example.com"] });

        scan.Refused.Should().BeFalse();
    }

    [Fact]
    public void A_changed_entry_without_its_integrity_is_refused()
    {
        var scan = Scan("package-lock.json", NpmLock, Express("https://registry.npmjs.org/express/-/express-4.19.2.tgz", "\"license\": \"MIT\""));

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileIntegrity);
    }

    [Fact]
    public void A_pnpm_resolution_without_integrity_is_refused()
    {
        const string before = "packages:\n\n  express@4.18.2:\n    resolution: {integrity: sha512-old}\n";
        var scan = Scan("pnpm-lock.yaml", before, "packages:\n\n  express@4.19.2:\n    resolution: {tarball: https://registry.npmjs.org/express/-/express-4.19.2.tgz}\n");

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileIntegrity);
    }

    [Fact]
    public void A_go_sum_line_without_its_hash_is_refused()
    {
        const string before = "github.com/google/uuid v1.3.0 h1:t6JiXgmwXMjEs8VusXIJk2BXHsn+wx8BZdTaoZ5fu7I=\n";
        var scan = Scan("go.sum", before, "github.com/google/uuid v1.4.0\n");

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileIntegrity);
    }

    [Fact]
    public void A_cargo_registry_version_without_its_checksum_is_refused()
    {
        const string before = "[[package]]\nname = \"serde\"\nversion = \"1.0.190\"\nsource = \"registry+https://github.com/rust-lang/crates.io-index\"\nchecksum = \"91d3c334ca1ee894a2c6f6ad698fe8c435b76d504b13d436f0685d648d6d96f7\"\n";
        var scan = Scan("Cargo.lock", before, before.Replace("1.0.190", "1.0.200", StringComparison.Ordinal));

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileIntegrity);
    }

    [Fact]
    public void A_cargo_git_source_is_refused()
    {
        const string before = "[[package]]\nname = \"serde\"\nversion = \"1.0.190\"\nsource = \"registry+https://github.com/rust-lang/crates.io-index\"\nchecksum = \"91d3c334ca1ee894a2c6f6ad698fe8c435b76d504b13d436f0685d648d6d96f7\"\n";
        var scan = Scan("Cargo.lock", before, before.Replace("registry+https://github.com/rust-lang/crates.io-index", "git+https://github.com/evil/serde#abc", StringComparison.Ordinal));

        scan.Reason.Should().Be(DependencyPatchReasons.LockfileSource);
    }

    [Fact]
    public void New_package_names_are_counted_against_the_base()
    {
        var after = NpmLock.Replace("  \"packages\": {\n", "  \"packages\": {\n    \"node_modules/express/node_modules/fresh-dep\": {\n      \"version\": \"1.0.0\"\n    },\n", StringComparison.Ordinal);

        var scan = Scan("package-lock.json", NpmLock, after);

        scan.Refused.Should().BeFalse();
        scan.NewPackages.Should().Equal("fresh-dep");
    }
}
