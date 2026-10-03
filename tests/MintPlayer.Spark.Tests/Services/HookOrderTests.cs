using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #482 replaced the numeric interceptor order (contributions F5) with two structural rules: a
/// <see cref="HookStage.Finalize"/> before-hook runs after every <see cref="HookStage.Default"/> one,
/// and at most one <see cref="IDeleteReplacement"/> decides a type's deletes. Within a stage, hooks
/// keep registration order (not a contract).
/// </summary>
public class HookOrderTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    [Fact]
    public async Task Finalize_hooks_run_after_every_default_hook_whatever_the_registration_order()
    {
        await using var factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(Guid.Parse("0f5e0000-0000-4000-8000-0f5e00000001"))],
            configureServices: services => services.AddSingleton(new InterceptionLog()),
            configureSpark: spark => spark
                // Deliberately backwards: the finalizer first.
                .AddHook<OrdFinalize>()
                .AddHook<OrdDefaultA>()
                .AddHook<OrdDefaultB>());

        using var scope = factory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ISparkHookPipeline>();

        pipeline.For<IBeforeSave>(typeof(InterceptedNote)).Select(h => h.GetType().Name).Should().Equal(
            nameof(OrdDefaultA), nameof(OrdDefaultB), nameof(OrdFinalize));
    }

    [Fact]
    public async Task Two_delete_replacements_for_one_type_are_refused()
    {
        await using var factory = new SparkEndpointFactory<InterceptedContext>(
            Store,
            [InterceptedNoteModel.For(Guid.Parse("0f5e0000-0000-4000-8000-0f5e00000002"))],
            configureServices: services => services.AddSingleton(new InterceptionLog()),
            configureSpark: spark => spark.AddHook<OrdReplacementA>().AddHook<OrdReplacementB>());

        using var scope = factory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ISparkHookPipeline>();

        var act = () => pipeline.For<IDeleteReplacement>(typeof(InterceptedNote));
        act.Should().Throw<InvalidOperationException>().WithMessage("*More than one IDeleteReplacement*");
    }

    /// <summary>History's "did this edit change anything?" must judge the entity after every hook that stamps or trims it.</summary>
    [Fact]
    public void History_stamps_in_the_finalize_stage()
    {
        var type = Type.GetType("MintPlayer.Spark.History.HistoryInterceptor, MintPlayer.Spark.History", throwOnError: true)!;
        var instance = (IBeforeSave)RuntimeHelpers.GetUninitializedObject(type);

        instance.Stage.Should().Be(HookStage.Finalize);
    }

    /// <summary>SoftDelete decides the replacement, so every before-delete hook sees it final (contributions keep a soft-deleted row's rows).</summary>
    [Fact]
    public void SoftDelete_is_the_delete_replacement()
    {
        var type = Type.GetType("MintPlayer.Spark.SoftDelete.SoftDeleteInterceptor, MintPlayer.Spark.SoftDelete", throwOnError: true)!;
        typeof(IDeleteReplacement).IsAssignableFrom(type).Should().BeTrue();
    }
}

public abstract class OrdBase : IBeforeSave
{
    public bool AppliesTo(Type entityType) => true;

    public ValueTask OnBeforeSaveAsync(SaveContext context) => ValueTask.CompletedTask;
}

public sealed class OrdFinalize : OrdBase, IBeforeSave { public HookStage Stage => HookStage.Finalize; }
public sealed class OrdDefaultA : OrdBase;
public sealed class OrdDefaultB : OrdBase;

public abstract class OrdReplacementBase : IDeleteReplacement
{
    public bool AppliesTo(Type entityType) => true;

    public ValueTask<bool> ReplaceAsync(DeleteContext context) => ValueTask.FromResult(false);
}

public sealed class OrdReplacementA : OrdReplacementBase;
public sealed class OrdReplacementB : OrdReplacementBase;
