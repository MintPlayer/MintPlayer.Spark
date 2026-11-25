using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Commands;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Commands;

/// <summary>Core's commands (#536 D34), registered by <c>AddSpark</c>. None of them opens a database connection.</summary>
internal static class SparkCoreCommands
{
    internal static IServiceCollection AddSparkCoreCommands(this IServiceCollection services)
    {
        services.AddSingleton(new SparkServiceRegistrations(services));

        services.AddSparkCommand<SparkModelCommand>(SparkDevelopmentExtensions.SynchronizeFlag,
            "Regenerates App_Data/Model and App_Data/modelHashes.json from the entity classes.");
        services.AddSparkCommand<SparkModelCommand>(SparkDevelopmentExtensions.VerifyFlag,
            "Writes nothing; exits 3 when the committed model has drifted from the entity classes (the merge-queue gate).");
        services.AddSparkCommand<SparkModelCommand>(SparkDescribeCommand.Flag,
            "--spark-describe <kind> [name] [--layers]: prints what a kind composes to, and optionally each leaf's layer.");
        services.AddSparkCommand<SparkSecurityInitCommand>(SparkSecurityInitExtensions.InitFlag,
            "Writes a starting App_Data/security.json that grants nothing; never overwrites one.");
        services.AddSparkCommand<SparkSecurityPostureCommand>(SparkSecurityVerificationExtensions.SynchronizeFlag,
            "Writes the security posture baseline (App_Data/securityPosture.txt).");
        services.AddSparkCommand<SparkSecurityPostureCommand>(SparkSecurityVerificationExtensions.VerifyFlag,
            "Writes nothing; exits 3 when the security posture has drifted from its baseline.");
        return services;
    }

    /// <summary>
    /// The model and posture code reports failure through <see cref="Environment.ExitCode"/>, as it did before the
    /// commands moved onto <see cref="ISparkCommand"/>; a command reads it back as its result.
    /// </summary>
    internal static int ExitCodeOf(Func<bool> run)
    {
        Environment.ExitCode = 0;
        run();
        return Environment.ExitCode;
    }
}

internal sealed partial class SparkModelCommand : ISparkCommand
{
    [Inject] private readonly SparkServiceRegistrations registrations;
    [Inject] private readonly IHostEnvironment environment;

    public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Task.FromResult(SparkCoreCommands.ExitCodeOf(() =>
            SparkDevelopmentExtensions.RunModelCommands(new SparkCommandHost(registrations.Services, environment), [.. args])));
}

internal sealed partial class SparkSecurityInitCommand : ISparkCommand
{
    [Inject] private readonly SparkServiceRegistrations registrations;
    [Inject] private readonly IHostEnvironment environment;

    public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Task.FromResult(SparkCoreCommands.ExitCodeOf(() =>
            SparkSecurityInitExtensions.RunInitSecurity(new SparkCommandHost(registrations.Services, environment), [.. args])));
}

internal sealed partial class SparkSecurityPostureCommand : ISparkCommand
{
    [Inject] private readonly SparkServiceRegistrations registrations;
    [Inject] private readonly IHostEnvironment environment;
    [Inject] private readonly IServiceProvider provider;

    public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Task.FromResult(SparkCoreCommands.ExitCodeOf(() =>
            SparkSecurityVerificationExtensions.RunSecurityPosture(new SparkCommandHost(registrations.Services, environment, provider), [.. args])));
}
