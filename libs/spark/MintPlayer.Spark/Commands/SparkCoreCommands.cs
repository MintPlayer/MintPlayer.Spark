using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Commands;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Commands;

/// <summary>Core's commands (#536 D34), registered by <c>AddSpark</c>. None of them opens a database connection.</summary>
internal static class SparkCoreCommands
{
    internal static IServiceCollection AddSparkCoreCommands(this IServiceCollection services)
    {
        services.AddSingleton(new SparkServiceRegistrations(services));

        services.AddSparkCommand<SparkModelCommand>(SparkDevelopmentExtensions.SynchronizeFlag,
            "Regenerates App_Data/Model and App_Data/modelHashes.json from the entity classes; on SQL Server and PostgreSQL also writes the migration for the change ([--name <migration>]).");
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
        services.AddSparkCommand<SparkMigrateCommand>(Services.SparkSchemaStartup.MigrateFlag,
            "Applies the schema migrations, deploys the indexes and runs the data migrations, then exits (a deploy step run with DDL rights).");
        services.AddSparkCommand<SparkMigrationScriptCommand>(Services.SparkSchemaStartup.ScriptFlag,
            "--spark-migration-script <file> [--from <migration>] [--to <migration>]: writes the idempotent schema script for a DBA.");
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
    [Inject] private readonly IServiceProvider provider;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var exitCode = SparkCoreCommands.ExitCodeOf(() =>
            SparkDevelopmentExtensions.RunModelCommands(new SparkCommandHost(registrations.Services, environment), [.. args]));
        // The model JSON first, then the migration for it (D24): a provider with a schema scaffolds one.
        if (exitCode != 0 || !args.Contains(SparkDevelopmentExtensions.SynchronizeFlag))
            return exitCode;
        return await SparkSchemaScaffolding.RunAsync(provider, environment, args, cancellationToken);
    }
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

/// <summary>
/// <c>--spark-migrate</c> (#536 D33 mode 2): applies the schema, deploys the indexes and runs the data migrations, then
/// exits. A pipeline step or init container runs it with a login that may change the schema, while the app runs with
/// <c>Spark:Database:ApplyMigrations=false</c>.
/// </summary>
internal sealed partial class SparkMigrateCommand : ISparkCommand
{
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly SparkModuleRegistry registry;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkSchemaMigrator>() is { } migrator)
        {
            await migrator.ApplyAsync(cancellationToken);
            Console.WriteLine("Schema migrations applied.");
        }

        // A data migration may query an index this release adds, as it can at startup.
        SparkExtensions.DeploySparkIndexes(services, registry.ResolveIndexAssemblies(), registry.IndexDeploymentFilter);

        if (services.GetService<ISparkDataMigrations>() is { } data)
        {
            await data.RunAsync(cancellationToken);
            Console.WriteLine("Data migrations applied.");
        }

        return 0;
    }
}

/// <summary>
/// <c>--spark-migration-script &lt;file&gt; [--from &lt;migration&gt;] [--to &lt;migration&gt;]</c> (#536 D33 mode 3): the
/// idempotent schema script for a DBA. Data migrations are code and are not in it: they run at the next start or with
/// <c>--spark-migrate</c>, and need only data rights.
/// </summary>
internal sealed partial class SparkMigrationScriptCommand : ISparkCommand
{
    [Inject] private readonly IServiceProvider services;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var flag = IndexOf(args, Services.SparkSchemaStartup.ScriptFlag);
        if (flag + 1 >= args.Count || args[flag + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Usage: {Services.SparkSchemaStartup.ScriptFlag} <file> [--from <migration>] [--to <migration>]");
            return 2;
        }

        var path = args[flag + 1];
        if (services.GetService<ISparkSchemaMigrator>() is not { } migrator)
        {
            Console.WriteLine($"The {services.GetService<ISparkStore>()?.ProviderName ?? "configured"} store keeps no schema, so there is no migration script; {path} was not written.");
            return 0;
        }

        await using (var output = new StreamWriter(path))
            await migrator.WriteScriptAsync(output, ValueOf(args, "--from"), ValueOf(args, "--to"), cancellationToken);

        Console.WriteLine($"Wrote the migration script to {path}.");
        return 0;
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string option)
        => IndexOf(args, option) is var i and >= 0 && i + 1 < args.Count ? args[i + 1] : null;
}
