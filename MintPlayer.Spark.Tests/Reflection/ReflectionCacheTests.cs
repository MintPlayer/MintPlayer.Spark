using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Tests.Reflection;

/// <summary>
/// Contract tests for the <see cref="ReflectionCache"/> primitive. The whole point of this
/// cache is to be safe under concurrent access and to give "factory runs exactly once per
/// key" semantics — a regression here silently re-runs reflection on every call and defeats
/// the cache. These tests pin the lock-free + Lazy contract.
/// </summary>
public class ReflectionCacheTests
{
    // Per-test isolation marker types — give each test its own TOwner generic-static cache
    // so concurrent runs don't see each other's entries.
    private sealed class OwnerA;
    private sealed class OwnerB;
    private sealed class OwnerConcurrency;
    private sealed class OwnerNullCache;
    private sealed class OwnerExceptionCache;
    private sealed class OwnerIsolation1;
    private sealed class OwnerIsolation2;

    [Fact]
    public void GetOrAdd_per_type_runs_factory_once_per_key()
    {
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<OwnerA, string>("k1", () => { Interlocked.Increment(ref calls); return "v1"; });
        var second = ReflectionCache.GetOrAdd<OwnerA, string>("k1", () => { Interlocked.Increment(ref calls); return "v1-DIFFERENT"; });

        first.Should().Be("v1");
        second.Should().Be("v1");
        calls.Should().Be(1, "because the factory must run exactly once per key");
    }

    [Fact]
    public void GetOrAdd_per_type_isolates_different_owners()
    {
        ReflectionCache.GetOrAdd<OwnerIsolation1, string>("shared-key", () => "from-1");
        var fromOwner2 = ReflectionCache.GetOrAdd<OwnerIsolation2, string>("shared-key", () => "from-2");

        fromOwner2.Should().Be("from-2",
            "because each TOwner has its own dictionary; the same key must not collide across owners");
    }

    [Fact]
    public void GetOrAdd_global_string_keyed_runs_factory_once_per_key()
    {
        var key = $"{nameof(GetOrAdd_global_string_keyed_runs_factory_once_per_key)}:{Guid.NewGuid()}";
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<int>(key, () => { Interlocked.Increment(ref calls); return 42; });
        var second = ReflectionCache.GetOrAdd<int>(key, () => { Interlocked.Increment(ref calls); return 99; });

        first.Should().Be(42);
        second.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public void GetOrAdd_type_keyed_runs_factory_once_per_type()
    {
        ReflectionCache.ClearGlobalForTests();
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<string>(typeof(GetOrAddTypeKeyedFixture), t => { Interlocked.Increment(ref calls); return t.Name; });
        var second = ReflectionCache.GetOrAdd<string>(typeof(GetOrAddTypeKeyedFixture), t => { Interlocked.Increment(ref calls); return "DIFFERENT"; });

        first.Should().Be(nameof(GetOrAddTypeKeyedFixture));
        second.Should().Be(nameof(GetOrAddTypeKeyedFixture));
        calls.Should().Be(1);
    }

    private sealed class GetOrAddTypeKeyedFixture;

    [Fact]
    public async Task GetOrAdd_factory_runs_exactly_once_under_concurrent_access()
    {
        // The Lazy<T>+ExecutionAndPublication contract is the foundation of the cache's
        // promise. Verify directly: 64 threads racing to read the same key, factory body
        // counts its invocations, must be exactly 1.
        var key = $"concurrency:{Guid.NewGuid()}";
        var factoryRuns = 0;
        var barrier = new Barrier(64);

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return ReflectionCache.GetOrAdd<OwnerConcurrency, int>(key, () =>
            {
                Interlocked.Increment(ref factoryRuns);
                Thread.Sleep(20); // widen the race window
                return 7;
            });
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        factoryRuns.Should().Be(1, "Lazy<T>(ExecutionAndPublication) guarantees single execution");
        results.Should().AllBeEquivalentTo(7);
    }

    [Fact]
    public void GetOrAdd_caches_null_result_negative_caching()
    {
        // ActionsResolver.FindActionsType depends on this: caching a "no such type" answer
        // is the entire point of moving it onto ReflectionCache.
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<OwnerNullCache, string?>("missing", () => { Interlocked.Increment(ref calls); return null; });
        var second = ReflectionCache.GetOrAdd<OwnerNullCache, string?>("missing", () => { Interlocked.Increment(ref calls); return "not-null-this-time"; });

        first.Should().BeNull();
        second.Should().BeNull();
        calls.Should().Be(1, "null is a valid cached value, factory must not re-run");
    }

    [Fact]
    public void GetOrAdd_caches_thrown_exception_and_rethrows()
    {
        // Documented behavior of Lazy<T>(ExecutionAndPublication): a throwing factory caches
        // the exception. We rely on this so that genuinely-missing reflection lookups fail
        // deterministically — the alternative (retry every call) would mask bugs.
        var calls = 0;

        Action firstAttempt = () => ReflectionCache.GetOrAdd<OwnerExceptionCache, string>("boom", () =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("reflection failure");
        });

        firstAttempt.Should().Throw<InvalidOperationException>().WithMessage("reflection failure");

        Action secondAttempt = () => ReflectionCache.GetOrAdd<OwnerExceptionCache, string>("boom", () =>
        {
            Interlocked.Increment(ref calls);
            return "would-succeed";
        });

        secondAttempt.Should().Throw<InvalidOperationException>().WithMessage("reflection failure");
        calls.Should().Be(1, "the cached exception is rethrown without re-running the factory");
    }

    [Fact]
    public void GetOrAdd_type_keyed_isolates_distinct_value_types_for_same_Type()
    {
        // The type-keyed overload is used by multiple call sites that cache different
        // things per Type (e.g. "collection element type of T" vs "LoadAsync<T> MethodInfo").
        // The cache must include TValue in its key so two such call sites can coexist
        // without an InvalidCastException at the boundary.
        ReflectionCache.ClearGlobalForTests();

        var asInt = ReflectionCache.GetOrAdd<int>(typeof(TypeKeyedCollisionFixture), _ => 7);
        var asString = ReflectionCache.GetOrAdd<string>(typeof(TypeKeyedCollisionFixture), t => t.Name);

        asInt.Should().Be(7);
        asString.Should().Be(nameof(TypeKeyedCollisionFixture));
    }

    private sealed class TypeKeyedCollisionFixture;

    [Fact]
    public void GetOrAdd_per_type_and_global_keyspaces_are_independent()
    {
        var key = $"shared-key-{Guid.NewGuid()}";

        ReflectionCache.GetOrAdd<OwnerA, string>(key, () => "from-per-type");
        var fromGlobal = ReflectionCache.GetOrAdd<string>(key, () => "from-global");

        fromGlobal.Should().Be("from-global",
            "the per-type and global tiers must use independent keyspaces");
    }

    [Fact]
    public void GetOrAdd_throws_on_null_key_or_factory()
    {
        // Guarding inputs at the boundary keeps null-related bugs from being cached as a
        // ghost entry under a coerced key.
        Action nullKey = () => ReflectionCache.GetOrAdd<OwnerA, string>(null!, () => "v");
        Action nullFactory = () => ReflectionCache.GetOrAdd<OwnerA, string>("k", null!);

        nullKey.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }
}
