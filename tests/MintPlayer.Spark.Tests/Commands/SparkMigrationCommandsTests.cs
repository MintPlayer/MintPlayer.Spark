using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Commands;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Commands;

/// <summary>
/// When the schema is applied (#536 D33, D34): at startup by default, refused at startup when
/// <c>Spark:Database:ApplyMigrations=false</c> leaves migrations pending, as a separate step with
/// <c>--spark-migrate</c>, or as a script with <c>--spark-migration-script</c>. A store without a schema (RavenDB,
/// in-memory) registers no <see cref="ISparkSchemaMigrator"/>, and every step treats that as nothing to do.
/// </summary>
public class SparkMigrationCommandsTests
{
    private sealed class FakeMigrator : ISparkSchemaMigrator
    {
        public List<string> Pending { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("pending");
            return Task.FromResult<IReadOnlyList<string>>([.. Pending]);
        }

        public Task ApplyAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("apply");
            Pending.Clear();
            return Task.CompletedTask;
        }

        public async Task WriteScriptAsync(TextWriter output, string? fromMigration, string? toMigration, CancellationToken cancellationToken = default)
        {
            Calls.Add($"script {fromMigration ?? "-"} {toMigration ?? "-"}");
            await output.WriteAsync("-- the script");
        }
    }

    private sealed class FakeDataMigrations(List<string> calls) : ISparkDataMigrations
    {
        public Task RunAsync(CancellationToken cancellationToken)
        {
            calls.Add("data");
            return Task.CompletedTask;
        }
    }

    private readonly FakeMigrator migrator = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    private ServiceProvider Provider(bool withMigrator = true, string? applyMigrations = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(applyMigrations is null ? [] : [new("Spark:Database:ApplyMigrations", applyMigrations)])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSpark(configuration, _ => { });
        if (withMigrator)
            services.AddSingleton<ISparkSchemaMigrator>(migrator);
        services.AddSingleton<ISparkDataMigrations>(new FakeDataMigrations(migrator.Calls));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Startup_applies_the_schema_by_default()
    {
        migrator.Pending.Add("20261125_Initial");
        using var provider = Provider();

        await SparkSchemaStartup.RunAsync(provider, CancellationToken.None);

        migrator.Calls.Should().Equal("apply");
    }

    [Fact]
    public async Task Startup_without_a_schema_migrator_does_nothing()
    {
        using var provider = Provider(withMigrator: false);

        await SparkSchemaStartup.RunAsync(provider, CancellationToken.None);

        migrator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task With_ApplyMigrations_off_startup_refuses_pending_migrations_and_names_both_commands()
    {
        migrator.Pending.Add("20261125_Initial");
        using var provider = Provider(applyMigrations: "false");

        var start = () => SparkSchemaStartup.RunAsync(provider, CancellationToken.None);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*20261125_Initial*--spark-migrate*--spark-migration-script*");
        migrator.Calls.Should().Equal("pending");
    }

    [Fact]
    public async Task With_ApplyMigrations_off_and_nothing_pending_startup_continues_without_applying()
    {
        using var provider = Provider(applyMigrations: "false");

        await SparkSchemaStartup.RunAsync(provider, CancellationToken.None);

        migrator.Calls.Should().Equal("pending");
    }

    [Fact]
    public async Task Migrate_applies_the_schema_then_the_data_migrations()
    {
        migrator.Pending.Add("20261125_Initial");
        using var provider = Provider(applyMigrations: "false");

        (await SparkCommandRunner.RunAsync(provider, ["--spark-migrate"], output, error, CancellationToken.None)).Should().Be(0);

        migrator.Calls.Should().Equal("apply", "data");
    }

    [Fact]
    public async Task Migrate_without_a_schema_migrator_still_runs_the_data_migrations()
    {
        using var provider = Provider(withMigrator: false);

        (await SparkCommandRunner.RunAsync(provider, ["--spark-migrate"], output, error, CancellationToken.None)).Should().Be(0);

        migrator.Calls.Should().Equal("data");
    }

    [Fact]
    public async Task The_script_command_writes_the_providers_script_with_its_range()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spark-script-{Guid.NewGuid():N}.sql");
        try
        {
            using var provider = Provider();

            (await SparkCommandRunner.RunAsync(provider, ["--spark-migration-script", path, "--from", "A", "--to", "B"], output, error, CancellationToken.None))
                .Should().Be(0);

            migrator.Calls.Should().Equal("script A B");
            File.ReadAllText(path).Should().Be("-- the script");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_script_command_needs_a_file()
    {
        using var provider = Provider();

        (await SparkCommandRunner.RunAsync(provider, ["--spark-migration-script"], output, error, CancellationToken.None)).Should().Be(2);

        migrator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_schema_the_script_command_writes_nothing_and_says_so()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spark-script-{Guid.NewGuid():N}.sql");
        using var provider = Provider(withMigrator: false);

        (await SparkCommandRunner.RunAsync(provider, ["--spark-migration-script", path], output, error, CancellationToken.None)).Should().Be(0);

        File.Exists(path).Should().BeFalse();
    }
}
