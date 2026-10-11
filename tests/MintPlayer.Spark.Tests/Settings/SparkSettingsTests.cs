using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Models;
using MintPlayer.Spark.Settings.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Settings;

/// <summary>
/// Spike S11 / X9 (<c>docs/seo_analyzer_PRD.md</c>): <see cref="ISparkSettings"/> reads the stored value,
/// else configuration, else the code default; stored and configured strings parse alike; a bad stored
/// value never throws; the snapshot is refreshed after the cache duration (a fake clock) and at once on
/// the instance that invalidates it; the seeder is idempotent and never writes <c>Value</c>.
/// </summary>
public class SparkSettingsTests : SparkTestDriver
{
    public static class StSettings
    {
        public static readonly SparkSetting<int> MaxPages = new("Seo:Limits:Unverified:MaxPages", 5, "Pages per audit of an unverified site.");
        public static readonly SparkSetting<TimeSpan> CoolDown = new("Seo:Limits:CoolDown", TimeSpan.FromHours(6), "Wait between two audits.");
        public static readonly SparkSetting<bool> IntrusiveProbes = new("Seo:Features:IntrusiveProbes", false, "Run intrusive probes.");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private static SparkSettingsCatalog Catalog()
    {
        var catalog = new SparkSettingsCatalog();
        catalog.AddFrom(typeof(StSettings));
        return catalog;
    }

    private SparkSettings Service(Dictionary<string, string?>? configuration = null, TimeProvider? clock = null, ILogger<SparkSettings>? logger = null)
        => new(
            Store,
            Catalog(),
            Options.Create(new SparkSettingsOptions()),
            logger ?? NullLogger<SparkSettings>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(),
            clock);

    private async Task StoreValueAsync(string key, string? value)
    {
        using var session = Store.OpenAsyncSession();
        var setting = await session.LoadAsync<Setting>(Setting.IdFor(key)) ?? new Setting { Id = Setting.IdFor(key), Key = key };
        setting.Value = value;
        await session.StoreAsync(setting, setting.Id);
        await session.SaveChangesAsync();
    }

    private static readonly Dictionary<string, string?> MaxPagesTen = new() { ["Seo:Limits:Unverified:MaxPages"] = "10" };

    // ---- coalescing ------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_value_overrides_configuration()
    {
        await StoreValueAsync(StSettings.MaxPages.Key, "25");

        (await Service(MaxPagesTen).GetAsync(StSettings.MaxPages)).Should().Be(25);
    }

    [Fact]
    public async Task Clearing_the_stored_value_falls_back_to_configuration()
    {
        await StoreValueAsync(StSettings.MaxPages.Key, "25");
        await StoreValueAsync(StSettings.MaxPages.Key, "");

        (await Service(MaxPagesTen).GetAsync(StSettings.MaxPages)).Should().Be(10);
    }

    [Fact]
    public async Task Deleting_the_stored_value_falls_back_to_configuration()
    {
        await StoreValueAsync(StSettings.MaxPages.Key, "25");
        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(Setting.IdFor(StSettings.MaxPages.Key));
            await session.SaveChangesAsync();
        }

        (await Service(MaxPagesTen).GetAsync(StSettings.MaxPages)).Should().Be(10);
    }

    [Fact]
    public async Task Without_a_stored_value_or_configuration_the_default_applies()
    {
        var settings = Service();

        (await settings.GetAsync(StSettings.MaxPages)).Should().Be(5);
        (await settings.GetAsync(StSettings.CoolDown)).Should().Be(TimeSpan.FromHours(6));
        (await settings.GetAsync(StSettings.IntrusiveProbes)).Should().BeFalse();
    }

    [Theory]
    [InlineData("00:05:00", "true")]
    [InlineData("1.02:00:00", "True")]
    public async Task TimeSpan_and_bool_parse_the_same_from_both_sources(string coolDown, string probes)
    {
        var fromConfiguration = Service(new()
        {
            ["Seo:Limits:CoolDown"] = coolDown,
            ["Seo:Features:IntrusiveProbes"] = probes,
        });
        var configuredCoolDown = await fromConfiguration.GetAsync(StSettings.CoolDown);
        var configuredProbes = await fromConfiguration.GetAsync(StSettings.IntrusiveProbes);

        await StoreValueAsync(StSettings.CoolDown.Key, coolDown);
        await StoreValueAsync(StSettings.IntrusiveProbes.Key, probes);
        var fromStore = Service();

        (await fromStore.GetAsync(StSettings.CoolDown)).Should().Be(configuredCoolDown);
        (await fromStore.GetAsync(StSettings.IntrusiveProbes)).Should().Be(configuredProbes);
        configuredCoolDown.Should().Be(TimeSpan.Parse(coolDown, System.Globalization.CultureInfo.InvariantCulture));
        configuredProbes.Should().BeTrue();
        // The configuration binder itself agrees.
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["v"] = coolDown }).Build()
            .GetValue<TimeSpan>("v").Should().Be(configuredCoolDown);
    }

    [Fact]
    public async Task A_stored_value_that_no_longer_parses_falls_back_with_a_warning_and_never_throws()
    {
        // As if the setting had been a string before and is an int now.
        await StoreValueAsync(StSettings.MaxPages.Key, "lots");
        var logger = new ListLogger<SparkSettings>();

        var value = await Service(MaxPagesTen, logger: logger).GetAsync(StSettings.MaxPages);

        value.Should().Be(10);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Seo:Limits:Unverified:MaxPages");
    }

    [Fact]
    public async Task An_undeclared_setting_is_a_programming_error()
    {
        var undeclared = new SparkSetting<int>("Seo:Undeclared", 1, "Not declared.");

        var act = async () => await Service().GetAsync(undeclared);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- cache -----------------------------------------------------------------------------------

    [Fact]
    public async Task Another_instance_sees_a_change_after_the_cache_duration_and_the_invalidating_instance_at_once()
    {
        var clock = new ManualClock();
        var reader = Service(MaxPagesTen, clock);
        var saver = Service(MaxPagesTen, clock);
        (await reader.GetAsync(StSettings.MaxPages)).Should().Be(10);
        (await saver.GetAsync(StSettings.MaxPages)).Should().Be(10);

        await StoreValueAsync(StSettings.MaxPages.Key, "25");
        saver.Invalidate(); // what the after-save interceptor does on the saving instance

        (await saver.GetAsync(StSettings.MaxPages)).Should().Be(25, "the saving instance drops its snapshot");
        clock.Now += TimeSpan.FromSeconds(29);
        (await reader.GetAsync(StSettings.MaxPages)).Should().Be(10, "another instance keeps its snapshot for 30 s");
        clock.Now += TimeSpan.FromSeconds(1);
        (await reader.GetAsync(StSettings.MaxPages)).Should().Be(25, "and reloads once it is 30 s old");
    }

    // ---- seeder ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_seeder_writes_one_document_per_key_is_idempotent_and_never_touches_Value()
    {
        var catalog = Catalog();
        var seeder = new SettingsSeeder(Store, catalog, Service(), NullLogger<SettingsSeeder>.Instance);

        (await seeder.SeedAsync(CancellationToken.None)).Should().Be(3);
        await StoreValueAsync(StSettings.MaxPages.Key, "25");
        using (var session = Store.OpenAsyncSession())
        {
            // A stale description from an older build, which the next seed refreshes.
            var stale = await session.LoadAsync<Setting>(Setting.IdFor(StSettings.CoolDown.Key));
            stale.Description = "old text";
            stale.Value = "00:10:00";
            await session.SaveChangesAsync();
        }

        (await seeder.SeedAsync(CancellationToken.None)).Should().Be(1, "only the stale description is rewritten");
        (await seeder.SeedAsync(CancellationToken.None)).Should().Be(0, "a second run changes nothing");

        using var check = Store.OpenAsyncSession();
        var all = await check.Advanced.LoadStartingWithAsync<Setting>("Settings/");
        all.Select(s => s.Id).Should().BeEquivalentTo(
            [Setting.IdFor(StSettings.MaxPages.Key), Setting.IdFor(StSettings.CoolDown.Key), Setting.IdFor(StSettings.IntrusiveProbes.Key)]);
        var maxPages = all.Single(s => s.Key == StSettings.MaxPages.Key);
        maxPages.Value.Should().Be("25");
        maxPages.Type.Should().Be("Int32");
        maxPages.Default.Should().Be("5");
        var coolDown = all.Single(s => s.Key == StSettings.CoolDown.Key);
        coolDown.Value.Should().Be("00:10:00");
        coolDown.Description.Should().Be("Wait between two audits.");
        coolDown.Default.Should().Be("06:00:00");
        all.Single(s => s.Key == StSettings.IntrusiveProbes.Key).Value.Should().BeNull();
    }

    // ---- declarations ----------------------------------------------------------------------------

    public static class BadKey
    {
        public static readonly SparkSetting<int> Slash = new("Seo/Limits", 1, "A slash would change the document id.");
    }

    public static class Clash
    {
        public static readonly SparkSetting<string> MaxPages = new("seo:limits:unverified:maxpages", "x", "Same key, other declaration.");
    }

    [Fact]
    public void A_malformed_or_twice_declared_key_is_refused_at_registration()
    {
        var catalog = Catalog();

        ((Action)(() => catalog.AddFrom(typeof(BadKey)))).Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Seo/Limits");
        ((Action)(() => catalog.AddFrom(typeof(Clash)))).Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("declared twice");
        catalog.AddFrom(typeof(StSettings)); // the same declarations again are fine
        catalog.All.Should().HaveCount(3);
    }
}
