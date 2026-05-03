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
    public void GetOrAdd_typed_key_runs_factory_once_per_type()
    {
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<Type, string>(typeof(GetOrAddTypeKeyedFixture), t => { Interlocked.Increment(ref calls); return t.Name; });
        var second = ReflectionCache.GetOrAdd<Type, string>(typeof(GetOrAddTypeKeyedFixture), t => { Interlocked.Increment(ref calls); return "DIFFERENT"; });

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
    public void GetOrAdd_typed_key_isolates_distinct_value_types_for_same_TKey()
    {
        // The identity-keyed tier is used by multiple call sites that may share a TKey
        // (e.g. "Type") for different TValues. The dictionary's inner discriminator
        // includes typeof(TValue) so two such call sites can coexist without an
        // InvalidCastException at the boundary.
        var asInt = ReflectionCache.GetOrAdd<Type, int>(typeof(TypeKeyedCollisionFixture), _ => 7);
        var asString = ReflectionCache.GetOrAdd<Type, string>(typeof(TypeKeyedCollisionFixture), t => t.Name);

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

    [Fact]
    public void GetOrAdd_global_throws_on_null_key_or_factory()
    {
        Action nullKey = () => ReflectionCache.GetOrAdd<string>((string)null!, () => "v");
        Action nullFactory = () => ReflectionCache.GetOrAdd<string>("k", (Func<string>)null!);

        nullKey.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetOrAdd_typed_key_throws_on_null_key_or_factory()
    {
        Action nullKey = () => ReflectionCache.GetOrAdd<Type, string>(null!, _ => "v");
        Action nullFactory = () => ReflectionCache.GetOrAdd<Type, string>(typeof(string), null!);

        nullKey.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ClearGlobalForTests_clears_global_string_keyed_entries()
    {
        var key = $"clearTest:{Guid.NewGuid()}";
        ReflectionCache.GetOrAdd<string>(key, () => "first");

        ReflectionCache.ClearGlobalForTests();

        // After clear, the factory must run again because the entry is gone.
        var calls = 0;
        var second = ReflectionCache.GetOrAdd<string>(key, () =>
        {
            Interlocked.Increment(ref calls);
            return "second";
        });

        second.Should().Be("second");
        calls.Should().Be(1, "the cleared entry should re-run its factory on next access");
    }

    [Fact]
    public void GlobalCount_increases_as_string_keyed_entries_are_added()
    {
        // GlobalCount only reflects the global string-keyed tier — the per-TOwner and
        // identity-keyed tiers are generic-static-specialized and have AppDomain lifetime
        // by design (see ReflectionCache.ClearGlobalForTests docstring).
        ReflectionCache.ClearGlobalForTests();
        ReflectionCache.GlobalCount.Should().Be(0);

        ReflectionCache.GetOrAdd<string>($"counter:{Guid.NewGuid()}", () => "a");
        ReflectionCache.GetOrAdd<string>($"counter:{Guid.NewGuid()}", () => "b");

        ReflectionCache.GlobalCount.Should().Be(2);
    }

    [Fact]
    public void GetOrAdd_typed_key_runs_factory_with_the_correct_key_argument()
    {
        // The factory receives the TKey value that was used — verify it isn't accidentally
        // swapped or boxed wrong by the (TKey, ValueType) wrapping.
        Type? observed = null;

        ReflectionCache.GetOrAdd<Type, string>(typeof(TypeArgFixture), t => { observed = t; return "v"; });

        observed.Should().Be(typeof(TypeArgFixture));
    }

    private sealed class TypeArgFixture;

    [Fact]
    public void GetOrAdd_typed_key_works_for_string_Type_string_tuple_shape()
    {
        // Framework call sites compose (string Op, Type, string Method)-shape tuple keys
        // when a cache purpose is identified by a label + a runtime Type + an instance
        // method name (e.g. StreamingQueryExecutor.ResolveStreamingMethod and
        // QueryExecutor.CustomQueryMethod). Pin the contract: ValueTuple's structural
        // equality + GetHashCode play correctly with the cache's per-TKey-type generic-static
        // specialization, including across the heterogeneous Type-vs-string members.
        var typeFromTypeof = typeof(TupleKeyFixture);
        var typeFromGetType = new TupleKeyFixture().GetType();
        var calls = 0;

        var first = ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), string>(
            ("MyOp", typeFromTypeof, "MyMethod"),
            k => { Interlocked.Increment(ref calls); return $"{k.Op}|{k.Type.Name}|{k.Method}"; });

        // Same conceptual key reached via a different Type-resolution route — should hit
        // the same dictionary slot and skip the factory.
        var second = ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), string>(
            ("MyOp", typeFromGetType, "MyMethod"),
            k => { Interlocked.Increment(ref calls); return "DIFFERENT"; });

        first.Should().Be("MyOp|TupleKeyFixture|MyMethod");
        second.Should().Be("MyOp|TupleKeyFixture|MyMethod");
        calls.Should().Be(1, "Type identity holds across resolution routes; the tuple cache must collapse them to one slot");

        // Differing on any tuple component → distinct slots.
        var differentOp = ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), string>(
            ("OtherOp", typeFromTypeof, "MyMethod"),
            k => { Interlocked.Increment(ref calls); return "differentOp"; });
        var differentMethod = ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), string>(
            ("MyOp", typeFromTypeof, "OtherMethod"),
            k => { Interlocked.Increment(ref calls); return "differentMethod"; });

        differentOp.Should().Be("differentOp");
        differentMethod.Should().Be("differentMethod");
        calls.Should().Be(3, "the leading Op string and the trailing Method string both discriminate within the dict");
    }

    private sealed class TupleKeyFixture;

    [Fact]
    public void GetOrAdd_per_type_caches_value_type_results()
    {
        // Per-type cache must round-trip value types via boxing without losing the value.
        var first = ReflectionCache.GetOrAdd<OwnerValueType, int>("k", () => 12345);
        var second = ReflectionCache.GetOrAdd<OwnerValueType, int>("k", () => 99999);

        first.Should().Be(12345);
        second.Should().Be(12345);
    }

    private sealed class OwnerValueType;

    [Fact]
    public void GetOrAdd_caches_complex_reference_type_results()
    {
        // PropertyInfo[] is a common cached shape (used by GetCachedProperties); verify
        // the cache returns the same array reference on repeat calls — that's how
        // downstream code can rely on cache hits not allocating new arrays.
        var first = ReflectionCache.GetOrAdd<OwnerComplexValue, string[]>("k", () => ["a", "b", "c"]);
        var second = ReflectionCache.GetOrAdd<OwnerComplexValue, string[]>("k", () => ["x", "y", "z"]);

        ReferenceEquals(first, second).Should().BeTrue("reference equality preserves cache-hit semantics for downstream callers");
        first.Should().Equal("a", "b", "c");
    }

    private sealed class OwnerComplexValue;
}
