using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// The implementation behind <c>--spark-init-security</c>, which scaffolds a first
/// <c>security.json</c>.
/// <para>
/// The refusal to overwrite is the part worth pinning. Since #310 <c>security.json</c> IS the
/// application's authorization model and the middleware will not start without it, so regenerating
/// a starter over a real one is the most destructive thing this command could do — silently, and
/// unrecoverable outside source control.
/// </para>
/// </summary>
public class SparkSecurityInitExtensionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-init-" + Guid.NewGuid().ToString("N"));

    public SparkSecurityInitExtensionsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
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

    private string SecurityPath => Path.Combine(root, "App_Data", "security.json");

    private WebApplicationBuilder Builder()
        => WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });

    [Fact]
    public void Without_the_flag_nothing_is_written()
    {
        var handled = Builder().InitializeSparkSecurityIfRequested(["--urls", "http://localhost:5000"]);

        handled.Should().BeFalse("the host must go on to start");
        File.Exists(SecurityPath).Should().BeFalse();
    }

    [Fact]
    public void The_starter_is_written_and_App_Data_is_created()
    {
        var handled = Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);

        handled.Should().BeTrue("a handled command stops the host from starting");
        File.Exists(SecurityPath).Should().BeTrue();
    }

    [Fact]
    public void The_starter_is_valid_json()
    {
        // It is read back by SecurityConfigurationLoader on the very next run, so a starter that
        // does not parse would turn "scaffold my security" into a startup crash.
        Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);

        var parse = () => JsonDocument.Parse(
            File.ReadAllText(SecurityPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        parse.Should().NotThrow("the file this command writes has to be loadable by the file it is written for");
    }

    [Fact]
    public void The_starter_grants_nothing()
    {
        // The whole design of the starter: one that granted something would be copied into
        // production by somebody who never read it. Empty fails visibly on the first request.
        Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);

        using var doc = JsonDocument.Parse(
            File.ReadAllText(SecurityPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        doc.RootElement.TryGetProperty("rights", out var rights).Should().BeTrue();
        rights.EnumerateArray().Should().BeEmpty("a starter that grants a right is a starter that ships");
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        // The destructive case. This must stay true even though the command reports success.
        Directory.CreateDirectory(Path.Combine(root, "App_Data"));
        const string RealModel = "{\"groups\":{},\"wellKnown\":{},\"rights\":[{\"id\":\"real\"}]}";
        File.WriteAllText(SecurityPath, RealModel);

        var handled = Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);

        handled.Should().BeTrue("the command was recognised, so the host must not go on to start");
        File.ReadAllText(SecurityPath).Should().Be(RealModel, "an existing authorization model is not a thing to regenerate over");
    }

    [Fact]
    public void Running_it_twice_is_safe()
    {
        Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);
        var first = File.ReadAllText(SecurityPath);

        Builder().InitializeSparkSecurityIfRequested(["--spark-init-security"]);

        File.ReadAllText(SecurityPath).Should().Be(first, "the second run hits the never-overwrite path");
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var builder = Builder();

        var nullArgs = () => builder.InitializeSparkSecurityIfRequested(null!);
        nullArgs.Should().Throw<ArgumentNullException>();

        var nullBuilder = () => ((WebApplicationBuilder)null!).InitializeSparkSecurityIfRequested([]);
        nullBuilder.Should().Throw<ArgumentNullException>();
    }
}
