using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Commands;
using MintPlayer.Spark.Commands;

namespace MintPlayer.Spark.Tests.Commands;

/// <summary>
/// The command runner (#536 D34, S20): the DI container is the registry. Each package registers its commands keyed by
/// their flag, the runner constructs only the requested one inside a scope, and <c>--spark-help</c> lists every flag
/// without constructing any.
/// </summary>
public class SparkCommandRunnerTests
{
    private sealed class Constructed
    {
        public List<string> Names { get; } = [];
    }

    private sealed class Dependency;

    private sealed class Greet : ISparkCommand
    {
        public Greet(Constructed constructed, Dependency _) => constructed.Names.Add(nameof(Greet));

        public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) => Task.FromResult(args.Count);
    }

    private sealed class Fail : ISparkCommand
    {
        public Fail(Constructed constructed) => constructed.Names.Add(nameof(Fail));

        public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) => Task.FromResult(3);
    }

    private sealed class Replacement : ISparkCommand
    {
        public Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) => Task.FromResult(42);
    }

    private readonly Constructed constructed = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    private ServiceProvider Provider(Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(constructed);
        services.AddScoped<Dependency>();
        services.AddSparkCommand<Greet>("--spark-greet", "Says hello.");
        services.AddSparkCommand<Fail>("--spark-fail", "Always fails.");
        more?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private Task<int?> RunAsync(ServiceProvider provider, params string[] args)
        => SparkCommandRunner.RunAsync(provider, args, output, error, CancellationToken.None);

    [Fact]
    public async Task Without_a_spark_flag_nothing_runs_and_nothing_is_constructed()
    {
        using var provider = Provider();

        (await RunAsync(provider, "--urls", "http://localhost:5000")).HasValue.Should().BeFalse();
        constructed.Names.Should().BeEmpty();
    }

    [Fact]
    public async Task The_requested_command_runs_in_a_scope_and_returns_its_exit_code()
    {
        using var provider = Provider();

        (await RunAsync(provider, "--spark-greet", "extra")).Should().Be(2);
    }

    [Fact]
    public async Task Only_the_requested_command_is_constructed()
    {
        using var provider = Provider();

        (await RunAsync(provider, "--spark-fail")).Should().Be(3);
        constructed.Names.Should().Equal(nameof(Fail));
    }

    [Fact]
    public async Task Help_lists_every_flag_with_its_description_and_constructs_nothing()
    {
        using var provider = Provider();

        (await RunAsync(provider, "--spark-help")).Should().Be(0);
        output.ToString().Should().Contain("--spark-greet").And.Contain("Says hello.")
            .And.Contain("--spark-fail").And.Contain("Always fails.");
        constructed.Names.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_spark_flag_is_refused_rather_than_starting_the_host()
    {
        using var provider = Provider();

        (await RunAsync(provider, "--spark-greeet")).Should().Be(2);
        error.ToString().Should().Contain("--spark-greeet").And.Contain("--spark-help");
    }

    [Fact]
    public async Task The_last_registration_of_a_flag_wins()
    {
        using var provider = Provider(services => services.AddSparkCommand<Replacement>("--spark-greet", "Replaced."));

        (await RunAsync(provider, "--spark-greet")).Should().Be(42);
        (await RunAsync(provider, "--spark-help")).Should().Be(0);
        output.ToString().Should().Contain("Replaced.").And.NotContain("Says hello.");
    }

    [Fact]
    public void A_flag_must_start_with_the_spark_prefix()
    {
        var register = () => new ServiceCollection().AddSparkCommand<Greet>("--greet", "Says hello.");

        register.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddSpark_registers_the_core_commands()
    {
        var services = new ServiceCollection();
        services.AddSpark(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _ => { });

        var flags = services.Where(d => d.ServiceType == typeof(SparkCommandDescription))
            .Select(d => ((SparkCommandDescription)d.ImplementationInstance!).Flag).ToList();
        string[] expected = [
            "--spark-synchronize-model", "--spark-verify-model", "--spark-describe",
            "--spark-init-security", "--spark-synchronize-security", "--spark-verify-security"];
        expected.Except(flags).Should().BeEmpty();
        services.Should().Contain(d => d.ServiceType == typeof(ISparkCommand) && Equals(d.ServiceKey, "--spark-verify-security"));
    }
}
