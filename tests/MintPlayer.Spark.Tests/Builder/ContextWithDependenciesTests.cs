using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Model;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Pins issue #292: the offline model commands work on a <see cref="Type"/>, so an application may put
/// constructor dependencies on its <see cref="SparkContext"/>.
/// </summary>
/// <remarks>
/// The commands only ever read property <em>types</em>, so an instance carried nothing but its own
/// <c>GetType()</c> — while the parameterless constructor it demanded ruled out the natural home for a
/// user-scoped query. Note the asymmetry that made it a papercut: <c>UseContext&lt;TContext&gt;</c>
/// never had a <c>new()</c> constraint, so the compiler accepted such a context and only these
/// commands rejected it.
/// </remarks>
public class ContextWithDependenciesTests
{
    private sealed class CurrentUser
    {
        public string Id { get; init; } = "users/1";
    }

    /// <summary>A context shaped like the motivating use case: it depends on a service, and scopes a query by it.</summary>
    private sealed class ScopedContext(CurrentUser currentUser) : SparkContext
    {
        public IRavenQueryable<DepProbe> MyProbes => Session.Query<DepProbe>().Where(p => p.OwnerId == currentUser.Id);
    }

    private sealed class DepProbe
    {
        public string? Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private abstract class AbstractContext : SparkContext
    {
        public IRavenQueryable<DepProbe> Probes => Session.Query<DepProbe>();
    }

    [Fact]
    public void A_context_with_constructor_dependencies_synchronizes()
    {
        // Red before #292: exit 2, "has no public parameterless constructor".
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, ScopedContext>();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(0);
        File.Exists(Path.Combine(scratch.Path, "App_Data", "Model", "DepProbe.json")).Should().BeTrue(
            "the context's query root must be discovered without ever constructing the context");
    }

    [Fact]
    public void A_context_with_constructor_dependencies_verifies()
    {
        // The verify gate is the one that runs in CI, so it has to accept the same context.
        using var scratch = new ScratchContentRoot();

        var syncBuilder = scratch.CreateBuilder();
        syncBuilder.Services.AddScoped<SparkContext, ScopedContext>();
        syncBuilder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
        Environment.ExitCode = 0;

        var verifyBuilder = scratch.CreateBuilder();
        verifyBuilder.Services.AddScoped<SparkContext, ScopedContext>();
        var handled = verifyBuilder.SynchronizeSparkModelsIfRequested(["--spark-verify-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(0);
    }

    [Fact]
    public void The_generic_overload_accepts_a_context_with_constructor_dependencies()
    {
        // Red before #292 at COMPILE time — the overload carried a new() constraint.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested<ScopedContext>(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Synchronizing_the_base_SparkContext_type_is_rejected()
    {
        // The guard that replaces an accident. While the command instantiated the context,
        // Activator.CreateInstance threw on the abstract base; resolving a Type removes that barrier,
        // and the base type declares no query roots, so it would describe an empty model.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested<SparkContext>(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(2);
    }

    [Fact]
    public void Synchronizing_an_abstract_context_type_is_rejected()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested<AbstractContext>(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(2);
    }

    [Fact]
    public void A_context_with_no_query_roots_does_not_overwrite_an_existing_model_hash_file()
    {
        // The actual damage from a wrong context type is not a deleted model — no entity file is
        // removed. It is modelHashes.json being rewritten to certify an EMPTY model over a still
        // populated directory, which --spark-verify-model cannot detect (it derives both sides of
        // its comparison from the same type) and which therefore first appears as a startup failure
        // in Production.
        using var scratch = new ScratchContentRoot();

        var first = scratch.CreateBuilder();
        first.Services.AddScoped<SparkContext, ScopedContext>();
        first.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
        Environment.ExitCode = 0;

        var hashPath = ModelHashFile.PathFor(scratch.Path);
        File.Exists(hashPath).Should().BeTrue();
        var before = File.ReadAllBytes(hashPath);

        var second = scratch.CreateBuilder();
        second.Services.AddScoped<SparkContext, EmptyDepContext>();
        second.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        Environment.ExitCode.Should().Be(2);
        File.ReadAllBytes(hashPath).Should().Equal(before,
            "an empty context must not re-certify a populated model directory");
    }

    private sealed class EmptyDepContext : SparkContext;

    /// <summary>
    /// A throwaway content root plus a <see cref="WebApplicationBuilder"/> rooted at it, so
    /// synchronization writes into the temp directory rather than the test host's own folder.
    /// Also restores <see cref="Environment.ExitCode"/>, which these tests deliberately set.
    /// </summary>
    private sealed class ScratchContentRoot : IDisposable
    {
        private readonly int _previousExitCode = Environment.ExitCode;

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "spark-ctordeps-tests-" + Guid.NewGuid().ToString("N"));

        public ScratchContentRoot() => Directory.CreateDirectory(Path);

        public WebApplicationBuilder CreateBuilder() =>
            WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path });

        public void Dispose()
        {
            Environment.ExitCode = _previousExitCode;
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
