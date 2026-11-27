using System.Reflection;
using System.Runtime.CompilerServices;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// SPARK060 compares a feature's <c>[SparkRequiresStore]</c> with the <c>[SparkStoreProvider]</c> on the app's
/// <c>Use&lt;Provider&gt;()</c> call at compile time; <c>UseSpark()</c> compares it with the store's
/// <see cref="ISparkStore.Capabilities"/> at startup (#551 D13). The two must say the same thing. When they drifted,
/// <c>apps/SqlDemo</c> failed to build with "Spark.Messaging needs Subscriptions; the PostgreSql provider doesn't
/// offer it", for a store that has offered Subscriptions since #536 M7a and Revisions since S8.
/// </summary>
public class SparkStoreProviderAttributeTests
{
    public static TheoryData<Type, Type> Providers => new()
    {
        { typeof(SparkRavenDbExtensions), typeof(RavenSparkStore) },
        { typeof(SparkInMemoryExtensions), typeof(InMemorySparkStore) },
        { typeof(SparkSqlServerExtensions), typeof(RelationalSparkStore) },
        { typeof(SparkPostgreSqlExtensions), typeof(RelationalSparkStore) },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void The_provider_attribute_declares_what_the_store_offers(Type extensions, Type storeType)
    {
        var declared = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<SparkStoreProviderAttribute>())
            .OfType<SparkStoreProviderAttribute>()
            .ToList();
        declared.Should().ContainSingle($"{extensions.Name} has one Use<Provider>() call");

        // The capabilities are a constant of the store type: no connection is needed to read them.
        var store = (ISparkStore)RuntimeHelpers.GetUninitializedObject(storeType);

        declared[0].Capabilities.Should().Be(store.Capabilities, $"SPARK060 reads {extensions.Name}'s attribute and UseSpark() reads {storeType.Name}");
    }
}
