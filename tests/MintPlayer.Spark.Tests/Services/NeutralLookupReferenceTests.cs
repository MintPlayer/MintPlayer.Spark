using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Dynamic lookup references on the database <c>SPARK_TEST_DATABASE</c> picks (#536 PRD §3, "Lookup references"). A
/// lookup's values are one document the service loads, edits and stores. On a store with a schema that document needs a
/// table of its own, and on every store an edit has to be checked against the version it read: two administrators
/// adding values at once used to keep only the last one's.
/// </summary>
public class NeutralLookupReferenceTests : SparkTestDriver
{
    private SparkEndpointFactory? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory Factory => factory ??= new SparkEndpointFactory(Database, [], security: SparkTestSecurity.Permissive);

    /// <summary>One call = one scope, the way a request works.</summary>
    private async Task<T> CallAsync<T>(Func<ILookupReferenceService, Task<T>> call)
    {
        using var scope = Factory.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<ILookupReferenceService>());
    }

    private static LookupReferenceValueDto Value(string key) => new()
    {
        Key = key,
        Values = TranslatedString.Create(key),
        IsActive = true,
    };

    [Fact]
    public async Task A_value_is_added_and_read_back()
    {
        await CallAsync(s => s.AddValueAsync(nameof(NlColour), Value("red")));

        var lookup = await CallAsync(s => s.GetAsync(nameof(NlColour)));

        lookup!.Values.Select(v => v.Key).Should().Equal("red");
    }

    [Fact]
    public async Task Concurrent_adds_all_land()
    {
        await CallAsync(s => s.AddValueAsync(nameof(NlColour), Value("first")));

        string[] keys = ["green", "blue", "yellow", "purple"];
        await Task.WhenAll(keys.Select(key => Task.Run(() => CallAsync(s => s.AddValueAsync(nameof(NlColour), Value(key))))));

        var lookup = await CallAsync(s => s.GetAsync(nameof(NlColour)));
        lookup!.Values.Select(v => v.Key).Should().BeEquivalentTo(["first", .. keys]);
    }

    [Fact]
    public async Task An_update_and_a_delete_both_land()
    {
        await CallAsync(s => s.AddValueAsync(nameof(NlColour), Value("keep")));
        await CallAsync(s => s.AddValueAsync(nameof(NlColour), Value("drop")));

        var renamed = Value("kept");
        await Task.WhenAll(
            Task.Run(() => CallAsync(s => s.UpdateValueAsync(nameof(NlColour), "keep", renamed))),
            Task.Run(async () => { await CallAsync(async s => { await s.DeleteValueAsync(nameof(NlColour), "drop"); return true; }); }));

        var lookup = await CallAsync(s => s.GetAsync(nameof(NlColour)));
        lookup!.Values.Select(v => v.Key).Should().Equal("kept");
    }
}

public class NlColour : DynamicLookupReference
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    [SetsRequiredMembers]
    public NlColour()
    {
        Name = nameof(NlColour);
    }
}
