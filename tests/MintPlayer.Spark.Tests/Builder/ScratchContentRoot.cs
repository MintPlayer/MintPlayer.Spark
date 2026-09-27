using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// A throwaway content root plus a <see cref="WebApplicationBuilder"/> rooted at it, so
/// synchronization writes into the temp directory rather than the test host's own folder.
/// Also restores <see cref="Environment.ExitCode"/>, which these tests deliberately set.
/// </summary>
internal sealed class ScratchContentRoot : IDisposable
{
    private readonly int _previousExitCode = Environment.ExitCode;

    public ScratchContentRoot(string prefix = "spark-sync-tests-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string ModelPath => System.IO.Path.Combine(Path, "App_Data", "Model");

    public WebApplicationBuilder CreateBuilder() =>
        WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path });

    /// <summary>Runs <c>--spark-synchronize-model</c> for <typeparamref name="TContext"/>.</summary>
    public void Synchronize<TContext>() where TContext : SparkContext
    {
        var builder = CreateBuilder();
        builder.Services.AddScoped<SparkContext, TContext>();
        builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
    }

    /// <summary>
    /// Runs <c>--spark-verify-model</c> for <typeparamref name="TContext"/> and returns the exit code
    /// together with everything written to stderr.
    /// </summary>
    /// <remarks>
    /// Returns the reported text as well as the code, because the code alone is a weak assertion:
    /// several checks and the hash comparison all exit 3, so "it failed" does not establish that it
    /// failed for the reason under test.
    /// </remarks>
    public (int ExitCode, string Reported) Verify<TContext>() where TContext : SparkContext
        => Verify(builder => builder.Services.AddScoped<SparkContext, TContext>());

    /// <inheritdoc cref="Verify{TContext}"/>
    public (int ExitCode, string Reported) Verify(Action<WebApplicationBuilder> register, string[]? args = null)
    {
        Environment.ExitCode = 0;
        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            var verifyBuilder = CreateBuilder();
            register(verifyBuilder);
            verifyBuilder.SynchronizeSparkModelsIfRequested(args ?? ["--spark-verify-model"]);
        }
        finally
        {
            Console.SetError(previous);
        }

        return (Environment.ExitCode, captured.ToString());
    }

    public void Dispose()
    {
        Environment.ExitCode = _previousExitCode;
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Every class that sets <see cref="Environment.ExitCode"/> or swaps <see cref="Console.Error"/>
/// runs in this collection: both are process-global, so two such classes running in parallel read
/// each other's exit codes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessExitCodeCollection
{
    public const string Name = "Process exit code";
}
