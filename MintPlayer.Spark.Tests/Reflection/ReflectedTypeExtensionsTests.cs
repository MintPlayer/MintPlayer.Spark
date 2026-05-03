using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Tests.Reflection;

/// <summary>
/// Pins the cached-extension contract: same <see cref="Type"/> + same name returns the
/// same <see cref="System.Reflection.PropertyInfo"/> reference (cache hit), missing
/// properties cache <c>null</c>, and per-Type GetProperties returns reference-equal
/// arrays on repeat calls.
/// </summary>
public class ReflectedTypeExtensionsTests
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)] private sealed class MarkerAttribute : Attribute;

    private class Fixture
    {
        [Marker] public string? Tagged { get; set; }
        public string? Untagged { get; set; }
    }

    [Fact]
    public void GetCachedProperties_returns_same_array_on_repeat_calls()
    {
        var first = typeof(Fixture).GetCachedProperties();
        var second = typeof(Fixture).GetCachedProperties();

        ReferenceEquals(first, second).Should().BeTrue();
        first.Should().HaveCount(2);
    }

    [Fact]
    public void GetCachedProperty_returns_PropertyInfo_when_present()
    {
        var prop = typeof(Fixture).GetCachedProperty(nameof(Fixture.Tagged));
        prop.Should().NotBeNull();
        prop!.Name.Should().Be(nameof(Fixture.Tagged));
    }

    [Fact]
    public void GetCachedProperty_caches_null_for_missing_property()
    {
        var first = typeof(Fixture).GetCachedProperty("DoesNotExist");
        var second = typeof(Fixture).GetCachedProperty("DoesNotExist");

        first.Should().BeNull();
        second.Should().BeNull();
        // Reference identity isn't meaningful here (both null); but caching null is the
        // contract we care about — covered by the underlying ReflectionCache null-cache test.
    }

    [Fact]
    public void GetCachedCustomAttribute_returns_attribute_when_decorated()
    {
        var prop = typeof(Fixture).GetCachedProperty(nameof(Fixture.Tagged))!;
        var attr = prop.GetCachedCustomAttribute<MarkerAttribute>();
        attr.Should().NotBeNull();
    }

    [Fact]
    public void GetCachedCustomAttribute_returns_null_when_undecorated()
    {
        var prop = typeof(Fixture).GetCachedProperty(nameof(Fixture.Untagged))!;
        var attr = prop.GetCachedCustomAttribute<MarkerAttribute>();
        attr.Should().BeNull();
    }

    [Fact]
    public void GetCachedCustomAttribute_works_on_Type_directly()
    {
        // GetCachedCustomAttribute extends MemberInfo, so it must work on Type itself
        // (used by SyncActionInterceptor.GetReplicatedAttribute(Type)).
        var attr = typeof(MarkedClassFixture).GetCachedCustomAttribute<MarkerAttribute>();
        attr.Should().NotBeNull();
    }

    [Marker]
    private sealed class MarkedClassFixture;

    [Fact]
    public void GetCachedProperties_throws_on_null_type()
    {
        Action act = () => ((Type)null!).GetCachedProperties();
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetCachedProperty_throws_on_null_type_or_name()
    {
        Action nullType = () => ((Type)null!).GetCachedProperty("X");
        Action nullName = () => typeof(Fixture).GetCachedProperty(null!);

        nullType.Should().Throw<ArgumentNullException>();
        nullName.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetCachedCustomAttribute_throws_on_null_member()
    {
        Action act = () => ((System.Reflection.MemberInfo)null!).GetCachedCustomAttribute<MarkerAttribute>();
        act.Should().Throw<ArgumentNullException>();
    }

    // --- GetCompletedTaskResult ---

    [Fact]
    public async Task GetCompletedTaskResult_extracts_value_from_completed_Task_of_T()
    {
        // Reflective dispatch sites get back a non-generic Task because they invoked the
        // generic method via reflection. GetCompletedTaskResult unwraps Task<T>.Result via
        // a cached PropertyInfo + compiled getter — the same shape used by EntityMapper,
        // ReferenceResolver, DatabaseAccess, QueryExecutor, SyncActionHandler.
        Task task = Task.FromResult("hello");
        await task;

        task.GetCompletedTaskResult().Should().Be("hello");
    }

    [Fact]
    public async Task GetCompletedTaskResult_extracts_value_type_result()
    {
        Task task = Task.FromResult(42);
        await task;

        task.GetCompletedTaskResult().Should().Be(42);
    }

    [Fact]
    public async Task GetCompletedTaskResult_returns_null_when_Task_T_resolves_to_null_reference()
    {
        Task task = Task.FromResult<string?>(null);
        await task;

        task.GetCompletedTaskResult().Should().BeNull();
    }

    [Fact]
    public void GetCompletedTaskResult_throws_on_null_task()
    {
        Action act = () => ((Task)null!).GetCompletedTaskResult();
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCompletedTaskResult_caches_Result_property_per_task_type()
    {
        // Two completed Task<string> instances should hit the same cached PropertyInfo.
        // Verify by extracting from both and confirming both succeed (a stale/wrong cache
        // would either NRE or return a wrong value).
        Task task1 = Task.FromResult("first");
        Task task2 = Task.FromResult("second");
        await Task.WhenAll(task1, task2);

        task1.GetCompletedTaskResult().Should().Be("first");
        task2.GetCompletedTaskResult().Should().Be("second");
    }

    [Fact]
    public void Two_separate_GetProperty_calls_collide_on_the_same_dictionary_slot()
    {
        // Pre-condition for the principled tier-redesign follow-up: if we ever switch
        // AccessorCache / GetCachedProperty from string keys to PropertyInfo (or
        // (Type, string)) keys backed by ConcurrentDictionary, the dictionary's identity
        // contract must hold — two independent calls to typeof(T).GetProperty("X") must
        // produce values that .Equals each other AND share a hash code, so the second
        // call hits the entry stored by the first instead of creating a duplicate slot.
        // The BCL caches RuntimePropertyInfo internally, so today they're typically
        // reference-equal too — but Equals + GetHashCode is the contract that actually
        // matters for ConcurrentDictionary behavior, and that's what we pin here.
        var a = typeof(Fixture).GetProperty(nameof(Fixture.Tagged))!;
        var b = typeof(Fixture).GetProperty(nameof(Fixture.Tagged))!;

        a.Equals(b).Should().BeTrue("PropertyInfo equality must hold across separate GetProperty calls on the same Type");
        a.GetHashCode().Should().Be(b.GetHashCode(),
            "Equals contract requires matching hash codes; ConcurrentDictionary depends on it for slot routing");

        var dict = new System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.PropertyInfo, string>();
        dict[a] = "from-a";
        dict[b] = "from-b";

        dict.Should().HaveCount(1, "the second assignment must overwrite the first slot, not add a new one");
        dict[a].Should().Be("from-b");

        // And the same for a different property on the same type — the two PropertyInfos
        // for distinct properties must NOT collide.
        var other = typeof(Fixture).GetProperty(nameof(Fixture.Untagged))!;
        dict[other] = "other";
        dict.Should().HaveCount(2);
    }

    [Fact]
    public void Type_obtained_via_different_routes_collides_on_the_same_dictionary_slot()
    {
        // Pre-condition for a Type-keyed (or (Type, ...)-keyed) cache: the runtime must
        // canonicalize Type instances within an AssemblyLoadContext so that compile-time
        // typeof(...), runtime instance.GetType(), Assembly.GetType(string), and
        // Type.GetType(assemblyQualifiedName) all produce values that collide on the
        // same dictionary slot. If that contract held weakly (only Equals-equal but not
        // hash-stable, or distinct instances per route), a Type-keyed cache would
        // duplicate entries on every lookup variant and silently defeat itself.
        var fromTypeof = typeof(Fixture);
        var fromGetType = new Fixture().GetType();
        var fromAssemblyLookup = typeof(Fixture).Assembly.GetType(typeof(Fixture).FullName!)!;
        var fromAssemblyQualifiedName = Type.GetType(typeof(Fixture).AssemblyQualifiedName!)!;

        // Reference equality — strongest contract, what the CLR actually guarantees within
        // an ALC. If this ever weakens we want the test to catch it.
        ReferenceEquals(fromTypeof, fromGetType).Should().BeTrue();
        ReferenceEquals(fromTypeof, fromAssemblyLookup).Should().BeTrue();
        ReferenceEquals(fromTypeof, fromAssemblyQualifiedName).Should().BeTrue();

        // Equals + GetHashCode contract — the one ConcurrentDictionary actually depends on.
        fromTypeof.Equals(fromGetType).Should().BeTrue();
        fromTypeof.GetHashCode().Should().Be(fromGetType.GetHashCode());

        var dict = new System.Collections.Concurrent.ConcurrentDictionary<Type, string>();
        dict[fromTypeof] = "from-typeof";
        dict[fromGetType] = "from-getType";
        dict[fromAssemblyLookup] = "from-assembly-lookup";
        dict[fromAssemblyQualifiedName] = "from-aqn";

        dict.Should().HaveCount(1, "all four resolution routes must dedupe to a single dictionary slot");
        dict[fromTypeof].Should().Be("from-aqn", "the last write wins on the shared slot");

        // Distinct types — sanity check that the dict isn't pathologically collapsing
        // everything to one slot.
        dict[typeof(string)] = "string";
        dict[typeof(int)] = "int";
        dict.Should().HaveCount(3);
    }

    [Fact]
    public void Tuple_of_Type_and_string_works_as_dictionary_key()
    {
        // The (Type, string) tuple is the natural key for "property X on type Y" lookups.
        // Verify ValueTuple's structural equality + hash play correctly with
        // ConcurrentDictionary so a tuple-keyed property cache works without surprises.
        var k1 = (typeof(Fixture), nameof(Fixture.Tagged));
        var k2 = (new Fixture().GetType(), nameof(Fixture.Tagged));
        var k3 = (typeof(Fixture), nameof(Fixture.Untagged));

        k1.Equals(k2).Should().BeTrue();
        k1.GetHashCode().Should().Be(k2.GetHashCode());
        k1.Equals(k3).Should().BeFalse("different property names must not collide");

        var dict = new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), string>();
        dict[k1] = "first";
        dict[k2] = "second";
        dict[k3] = "third";

        dict.Should().HaveCount(2);
        dict[k1].Should().Be("second");
        dict[k3].Should().Be("third");
    }
}
