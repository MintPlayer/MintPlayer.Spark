using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// The durable after-commit outbox (#482, D17) without the Messaging runtime: each unit of work is
/// stored as a document in the write's own session, exactly as Messaging stores its message, and
/// <see cref="DrainAsync"/> runs the committed ones through the framework's dispatcher. A refused or
/// cancelled write commits no document, so its interceptors never run — the property under test.
/// </summary>
/// <remarks>
/// Register with <see cref="TestAfterCommitOutboxExtensions.AddTestAfterCommitOutbox"/>; singleton state, scoped outbox.
/// Works on every store (#536): the unit of work is a registered stored type, and the store picks its id.
/// </remarks>
public sealed class TestAfterCommitOutbox
{
    private readonly ConcurrentQueue<string> pending = new();

    /// <summary>The ids of every unit of work stored so far — committed or not.</summary>
    public IReadOnlyCollection<string> Enqueued => pending;

    internal void Add(string id) => pending.Enqueue(id);

    /// <summary>
    /// Runs every committed unit of work, oldest first, each in its own scope (as Messaging would), and
    /// forgets the ones that never committed. Returns how many ran.
    /// </summary>
    public async Task<int> DrainAsync(IServiceProvider services)
    {
        var ran = 0;
        var store = services.GetRequiredService<ISparkStore>();
        while (pending.TryDequeue(out var id))
        {
            SparkAfterCommitWork? work;
            using (var session = store.OpenSession())
                work = (await session.LoadAsync<Stored>(id))?.Work;
            if (work is null)
                continue;

            using var scope = services.CreateScope();
            var dispatched = await scope.ServiceProvider.GetRequiredService<ISparkAfterCommitDispatcher>().DispatchAsync(work, CancellationToken.None);
            if (!dispatched)
                throw new InvalidOperationException($"After-commit interceptor '{work.InterceptorType}' was not found.");
            ran++;
        }
        return ran;
    }

    internal sealed class Stored
    {
        public string? Id { get; set; }
        public SparkAfterCommitWork? Work { get; set; }
    }
}

internal sealed class TestAfterCommitOutboxSeam(TestAfterCommitOutbox state) : ISparkAfterCommitOutbox
{
    public async Task EnqueueAsync(ISparkSession session, SparkAfterCommitWork work, CancellationToken cancellationToken = default)
    {
        var stored = new TestAfterCommitOutbox.Stored { Work = work };
        await session.StoreAsync(stored, cancellationToken);
        state.Add(session.GetId(stored) ?? throw new InvalidOperationException("The store assigned the outbox entry no id."));
    }
}

public static class TestAfterCommitOutboxExtensions
{
    public static IServiceCollection AddTestAfterCommitOutbox(this IServiceCollection services)
    {
        services.AddSingleton<TestAfterCommitOutbox>();
        services.AddScoped<ISparkAfterCommitOutbox, TestAfterCommitOutboxSeam>();
        services.AddSparkStoredType<TestAfterCommitOutbox.Stored>();
        return services;
    }
}
