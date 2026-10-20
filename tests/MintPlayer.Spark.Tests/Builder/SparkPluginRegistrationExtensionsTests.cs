using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// X20 item 8: the public plugin registration helpers — a configuration section bound lazily from the
/// options type's own <c>SectionName</c>, a registration that is safe to call twice, a find-or-create
/// catalog, and provider slots that hold exactly one implementation.
/// </summary>
public class SparkPluginRegistrationExtensionsTests
{
    private sealed class WidgetOptions : ISparkConfigurationSection
    {
        public static string SectionName => "Spark:Widgets";
        public int Size { get; set; } = 1;
        public string? Label { get; set; }
    }

    private sealed class WidgetMarker;

    private sealed class WidgetCatalog
    {
        public List<string> Names { get; } = [];
    }

    private interface IWidgetSlot { }
    private sealed class FirstWidget : IWidgetSlot { }
    private sealed class SecondWidget : IWidgetSlot { }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void A_configuration_section_binds_from_the_options_types_own_SectionName()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration(("Spark:Widgets:Size", "7"), ("Spark:Widgets:Label", "big")));

        services.AddSparkConfigurationSection<WidgetOptions>();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<WidgetOptions>>().Value;
        options.Size.Should().Be(7);
        options.Label.Should().Be("big");
    }

    [Fact]
    public void Code_configured_after_the_section_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration(("Spark:Widgets:Size", "7")));

        services.AddSparkConfigurationSection<WidgetOptions>();
        services.Configure<WidgetOptions>(o => o.Size = 9);

        services.BuildServiceProvider().GetRequiredService<IOptions<WidgetOptions>>().Value.Size.Should().Be(9);
    }

    [Fact]
    public void Without_an_IConfiguration_the_defaults_stay()
    {
        var services = new ServiceCollection();

        services.AddSparkConfigurationSection<WidgetOptions>();

        services.BuildServiceProvider().GetRequiredService<IOptions<WidgetOptions>>().Value.Size.Should().Be(1);
    }

    [Fact]
    public void The_marker_is_registered_once_and_only_the_first_call_says_so()
    {
        var services = new ServiceCollection();

        services.TryAddSparkMarker<WidgetMarker>().Should().BeTrue();
        services.TryAddSparkMarker<WidgetMarker>().Should().BeFalse();

        services.Count(d => d.ServiceType == typeof(WidgetMarker)).Should().Be(1);
    }

    [Fact]
    public void The_singleton_is_created_once_and_later_calls_get_the_same_instance()
    {
        var services = new ServiceCollection();

        var first = services.GetOrAddSparkSingleton(() => new WidgetCatalog(), out var firstCreated);
        var second = services.GetOrAddSparkSingleton(() => new WidgetCatalog(), out var secondCreated);

        firstCreated.Should().BeTrue();
        secondCreated.Should().BeFalse();
        second.Should().BeSameAs(first);
        services.Count(d => d.ServiceType == typeof(WidgetCatalog)).Should().Be(1);
    }

    [Fact]
    public void A_singleton_registered_as_a_factory_is_refused_rather_than_duplicated()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new WidgetCatalog());

        var act = () => services.GetOrAddSparkSingleton(() => new WidgetCatalog(), out _);

        act.Should().Throw<InvalidOperationException>().WithMessage("*WidgetCatalog*");
    }

    [Fact]
    public void A_provider_slot_holds_only_the_last_implementation()
    {
        var services = new ServiceCollection();
        // Two earlier registrations: Replace would remove only the first and leave the second resolving.
        services.AddScoped<IWidgetSlot, FirstWidget>();
        services.AddScoped<IWidgetSlot, FirstWidget>();

        services.UseSparkProvider<IWidgetSlot, SecondWidget>(ServiceLifetime.Scoped);

        services.Where(d => d.ServiceType == typeof(IWidgetSlot)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be(typeof(SecondWidget));
        using var scope = services.BuildServiceProvider().CreateScope();
        scope.ServiceProvider.GetServices<IWidgetSlot>().Should().ContainSingle().Which.Should().BeOfType<SecondWidget>();
    }

    [Fact]
    public void A_provider_slot_takes_a_factory_too()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidgetSlot, FirstWidget>();

        services.UseSparkProvider<IWidgetSlot>(_ => new SecondWidget(), ServiceLifetime.Singleton);

        services.BuildServiceProvider().GetServices<IWidgetSlot>().Should().ContainSingle().Which.Should().BeOfType<SecondWidget>();
    }
}
