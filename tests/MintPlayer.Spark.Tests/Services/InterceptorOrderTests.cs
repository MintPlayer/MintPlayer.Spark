using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Contributions F5 — <see cref="IPersistentObjectInterceptor.Order"/>: before-hooks run by (Order,
/// registration index), after-hooks in the reverse, whatever order the packages were registered in.
/// </summary>
public class InterceptorOrderTests : SparkTestDriver
{
    [Fact]
    public async Task Reversed_registration_still_yields_the_documented_order_and_equal_orders_keep_registration_order()
    {
        await using var factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(Guid.Parse("0f5e0000-0000-4000-8000-0f5e00000001"))],
            configureServices: services => services.AddSingleton(new InterceptionLog()),
            configureSpark: spark => spark
                // Deliberately backwards against the documented scale.
                .AddPersistentObjectInterceptor<OrdContributions>()
                .AddPersistentObjectInterceptor<OrdDefaultA>()
                .AddPersistentObjectInterceptor<OrdModeration>()
                .AddPersistentObjectInterceptor<OrdDefaultB>()
                .AddPersistentObjectInterceptor<OrdHistory>()
                .AddPersistentObjectInterceptor<OrdSoftDelete>());

        using var scope = factory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IPersistentObjectInterceptorPipeline>();

        pipeline.For(typeof(InterceptedNote)).Select(i => i.GetType().Name).Should().Equal(
            nameof(OrdSoftDelete), nameof(OrdHistory), nameof(OrdModeration),
            nameof(OrdDefaultA), nameof(OrdDefaultB),   // equal Order: registration order
            nameof(OrdContributions));
    }

    /// <summary>
    /// The built-ins declare the scale themselves, so QnA's SoftDelete → History → Moderation holds
    /// even if an app registers them the other way round, and Contributions has room to run last.
    /// </summary>
    [Theory]
    [InlineData("MintPlayer.Spark.SoftDelete.SoftDeleteInterceptor, MintPlayer.Spark.SoftDelete", PersistentObjectInterceptorOrder.SoftDelete)]
    [InlineData("MintPlayer.Spark.History.HistoryInterceptor, MintPlayer.Spark.History", PersistentObjectInterceptorOrder.History)]
    [InlineData("MintPlayer.Spark.Moderation.Services.ModerationInterceptor, MintPlayer.Spark.Moderation", PersistentObjectInterceptorOrder.Moderation)]
    public void The_built_in_interceptors_declare_their_place_on_the_scale(string typeName, int expected)
    {
        var type = Type.GetType(typeName, throwOnError: true)!;
        var instance = (IPersistentObjectInterceptor)RuntimeHelpers.GetUninitializedObject(type);

        instance.Order.Should().Be(expected);
    }

    [Fact]
    public void The_scale_leaves_Contributions_last()
    {
        int[] scale =
        [
            PersistentObjectInterceptorOrder.SoftDelete, PersistentObjectInterceptorOrder.History,
            PersistentObjectInterceptorOrder.Moderation, PersistentObjectInterceptorOrder.Default,
            PersistentObjectInterceptorOrder.Contributions,
        ];
        scale.Should().Equal(scale.Order().ToArray());
        scale.Distinct().Should().HaveCount(scale.Length);
    }
}

public abstract class OrdBase : IPersistentObjectInterceptor
{
    public bool AppliesTo(Type entityType) => true;
}

public sealed class OrdSoftDelete : OrdBase, IPersistentObjectInterceptor { public int Order => PersistentObjectInterceptorOrder.SoftDelete; }
public sealed class OrdHistory : OrdBase, IPersistentObjectInterceptor { public int Order => PersistentObjectInterceptorOrder.History; }
public sealed class OrdModeration : OrdBase, IPersistentObjectInterceptor { public int Order => PersistentObjectInterceptorOrder.Moderation; }
public sealed class OrdDefaultA : OrdBase;
public sealed class OrdDefaultB : OrdBase;
public sealed class OrdContributions : OrdBase, IPersistentObjectInterceptor { public int Order => PersistentObjectInterceptorOrder.Contributions; }
