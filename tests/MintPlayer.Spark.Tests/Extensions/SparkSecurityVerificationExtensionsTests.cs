using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Extensions;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// The implementation behind <c>--spark-verify-security</c> and
/// <c>--spark-synchronize-security</c>.
/// <para>
/// This gate is the only thing standing between a one-line <c>security.json</c> diff and a publicly
/// reachable endpoint: CI runs it per app, and a non-zero exit is what stops the pull request. A
/// false negative here is invisible by construction — the build stays green and the surface widens
/// quietly — so the failure paths matter more than the happy one, and every case below asserts the
/// exit code rather than only the file contents.
/// </para>
/// </summary>
public class SparkSecurityVerificationExtensionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-posture-" + Guid.NewGuid().ToString("N"));

    public SparkSecurityVerificationExtensionsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        Environment.ExitCode = 0;
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        GC.SuppressFinalize(this);
    }

    private string BaselinePath => Path.Combine(root, "App_Data", "securityPosture.txt");

    private void WriteBaseline(string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "App_Data"));
        File.WriteAllText(BaselinePath, content);
    }

    /// <summary>
    /// A builder rooted at a scratch directory, with only the reporter registered — which is all the
    /// production path resolves, and the reason the real command needs no database.
    /// </summary>
    private WebApplicationBuilder Builder(params string[] anonymouslyReachable)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });

        var reporter = Substitute.For<ISecurityPostureReporter>();
        reporter.Describe().Returns(new SecurityPosture([.. anonymouslyReachable], []));
        builder.Services.AddSingleton(reporter);

        return builder;
    }

    /// <summary>A builder with no Spark services at all — the container was never configured.</summary>
    private WebApplicationBuilder BuilderWithoutSpark()
        => WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });

    [Fact]
    public void Neither_flag_means_the_host_starts_normally()
    {
        var handled = Builder("Query/Car").VerifySparkSecurityIfRequested(["--urls", "http://localhost:5000"]);

        handled.Should().BeFalse("the host must go on to start when no security command was asked for");
        Environment.ExitCode.Should().Be(0);
        File.Exists(BaselinePath).Should().BeFalse("verify and synchronize are the only things that touch the baseline");
    }

    [Fact]
    public void Synchronize_writes_the_baseline_and_creates_App_Data()
    {
        var handled = Builder("Read/Car", "Query/Car").VerifySparkSecurityIfRequested(["--spark-synchronize-security"]);

        handled.Should().BeTrue("a handled command stops the host from starting");
        Environment.ExitCode.Should().Be(0);
        File.ReadAllText(BaselinePath).Should().Be("Read/Car\nQuery/Car\n");
    }

    [Fact]
    public void Verify_passes_when_the_surface_is_unchanged()
    {
        WriteBaseline("Query/Car\nRead/Car\n");

        Builder("Query/Car", "Read/Car").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Verify_fails_when_the_anonymous_surface_widens()
    {
        // The case the gate exists for: one extra grant reaching the anonymous group.
        WriteBaseline("Query/Car\n");

        Builder("Query/Car", "Read/Secret").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(3);
    }

    [Fact]
    public void Verify_also_fails_when_the_surface_narrows()
    {
        // Narrowing is safe but still unreviewed, and a gate that only catches widening would let
        // the baseline drift out of step with reality until it stopped meaning anything.
        WriteBaseline("Query/Car\nRead/Car\n");

        Builder("Query/Car").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(3);
    }

    [Fact]
    public void A_missing_baseline_is_drift_rather_than_a_pass()
    {
        // Otherwise deleting the file would be the way to silence the gate.
        File.Exists(BaselinePath).Should().BeFalse();

        Builder("Query/Car").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(3);
    }

    [Fact]
    public void Line_endings_do_not_count_as_drift()
    {
        // The baseline is committed on Windows and verified on ubuntu-latest, so a CRLF checkout
        // must not fail the gate — that would make CI red for everyone with no security meaning.
        WriteBaseline("Query/Car\r\nRead/Car\r\n");

        Builder("Query/Car", "Read/Car").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(0);
    }

    [Fact]
    public void A_trailing_newline_difference_does_not_count_as_drift()
    {
        WriteBaseline("Query/Car");

        Builder("Query/Car").VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(0);
    }

    [Fact]
    public void An_empty_anonymous_surface_round_trips()
    {
        // The end state the CodeCoverage authorization migration is heading for: nothing reachable
        // without signing in. It must be representable, and must not read as a missing baseline.
        Builder().VerifySparkSecurityIfRequested(["--spark-synchronize-security"]);
        File.ReadAllText(BaselinePath).Should().Be("(nothing)\n");

        Environment.ExitCode = 0;
        Builder().VerifySparkSecurityIfRequested(["--spark-verify-security"]);
        Environment.ExitCode.Should().Be(0, "synchronize then verify must agree — the gate has to be a fixed point");
    }

    [Fact]
    public void Locking_down_a_previously_public_surface_is_reported_as_drift()
    {
        WriteBaseline("Query/Car\nRead/Car\n");

        Builder().VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        Environment.ExitCode.Should().Be(3, "going to zero is still a change the reviewer should see");
    }

    [Fact]
    public void A_container_without_Spark_exits_misconfigured_rather_than_throwing()
    {
        // Distinguishable from drift on purpose: exit 2 says "you did not call AddSpark", exit 3
        // says "the surface moved". Collapsing them would send someone hunting a security change
        // that never happened.
        var handled = BuilderWithoutSpark().VerifySparkSecurityIfRequested(["--spark-verify-security"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(2);
    }

    [Fact]
    public void Both_flags_at_once_verifies_rather_than_rewriting_the_baseline()
    {
        // Verify wins, which is the safe precedence: a CI invocation that somehow carried both must
        // not silently rewrite the very file it is checking.
        WriteBaseline("Query/Car\n");

        Builder("Query/Car", "Read/Secret")
            .VerifySparkSecurityIfRequested(["--spark-verify-security", "--spark-synchronize-security"]);

        Environment.ExitCode.Should().Be(3);
        File.ReadAllText(BaselinePath).Should().Be("Query/Car\n", "the baseline must be untouched by a verify");
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var builder = Builder();

        var nullArgs = () => builder.VerifySparkSecurityIfRequested(null!);
        nullArgs.Should().Throw<ArgumentNullException>();

        var nullBuilder = () => ((WebApplicationBuilder)null!).VerifySparkSecurityIfRequested([]);
        nullBuilder.Should().Throw<ArgumentNullException>();
    }
}
