using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Commands;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// The builder-phase entry points the tests drive. The apps reach the same code through <see cref="ISparkCommand"/> and
/// <c>app.RunSparkCommandIfRequestedAsync(args)</c> since #536 D34; these keep the tests on a builder they control, with
/// no host built. Each returns <see langword="true"/> when a flag it knows was present, and reports through
/// <see cref="Environment.ExitCode"/>, as before.
/// </summary>
internal static class SparkCommandTestExtensions
{
    public static bool SynchronizeSparkModelsIfRequested(this WebApplicationBuilder builder, string[] args)
        => SparkDevelopmentExtensions.RunModelCommands(new SparkCommandHost(builder.Services, builder.Environment), args);

    public static bool SynchronizeSparkModelsIfRequested<TContext>(this WebApplicationBuilder builder, string[] args)
        where TContext : SparkContext
        => SparkDevelopmentExtensions.RunModelCommands(new SparkCommandHost(builder.Services, builder.Environment), args, typeof(TContext));

    public static bool InitializeSparkSecurityIfRequested(this WebApplicationBuilder builder, string[] args)
        => SparkSecurityInitExtensions.RunInitSecurity(new SparkCommandHost(builder.Services, builder.Environment), args);

    public static bool VerifySparkSecurityIfRequested(this WebApplicationBuilder builder, string[] args)
        => SparkSecurityVerificationExtensions.RunSecurityPosture(new SparkCommandHost(builder.Services, builder.Environment), args);
}
