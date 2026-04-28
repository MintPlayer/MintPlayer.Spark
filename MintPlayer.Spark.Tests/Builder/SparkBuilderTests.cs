using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Pins the construction contract of <see cref="SparkBuilder"/>. The class is
/// instantiated by <c>SparkMiddleware.AddSpark</c> in two shapes — with and
/// without an <see cref="IConfiguration"/> — and both go through the
/// source-generated [Inject] ctor. A regression in either path silently breaks
/// AddSpark for one of the two host shapes (configuration-aware vs. config-free).
/// </summary>
public class SparkBuilderTests
{
    [Fact]
    public void Constructs_with_services_and_configuration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var builder = new SparkBuilder(services, configuration);

        builder.Services.Should().BeSameAs(services);
        builder.Configuration.Should().BeSameAs(configuration);
    }

    [Fact]
    public void Constructs_with_services_only_treats_configuration_as_null()
    {
        var services = new ServiceCollection();

        var builder = new SparkBuilder(services);

        builder.Services.Should().BeSameAs(services);
        builder.Configuration.Should().BeNull();
    }

    [Fact]
    public void Registry_and_Options_are_initialized_to_fresh_defaults()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.Registry.Should().NotBeNull();
        builder.Options.Should().NotBeNull();
    }

    [Fact]
    public void Implements_ISparkBuilder_so_extension_methods_compose()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.Should().BeAssignableTo<ISparkBuilder>();
    }
}
