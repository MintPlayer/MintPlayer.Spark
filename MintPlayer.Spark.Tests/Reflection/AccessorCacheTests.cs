using System.Reflection;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Tests.Reflection;

/// <summary>
/// Pins the contract of compiled-delegate accessors: they must produce the same
/// values as raw <see cref="PropertyInfo.GetValue(object?)"/> /
/// <see cref="PropertyInfo.SetValue(object?, object?)"/> across reference types,
/// value types, nullables, and enums — and the compiled delegates must be cached
/// (same <see cref="PropertyInfo"/> ⇒ same delegate instance).
/// </summary>
public class AccessorCacheTests
{
    private class RefFixture
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public DayOfWeek Day { get; set; }
        public Guid? OptionalId { get; set; }
        public string ReadOnly => "read-only";
        public string WriteOnly { set { /* discard */ } }
    }

    private struct StructFixture
    {
        public int Value { get; set; }
    }

    [Fact]
    public void GetGetter_reads_reference_type_property()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Name))!;
        var getter = AccessorCache.GetGetter(prop);

        var instance = new RefFixture { Name = "Alice" };

        getter(instance).Should().Be("Alice");
    }

    [Fact]
    public void GetSetter_writes_reference_type_property()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Name))!;
        var setter = AccessorCache.GetSetter(prop);

        var instance = new RefFixture();
        setter(instance, "Bob");

        instance.Name.Should().Be("Bob");
    }

    [Fact]
    public void GetGetter_and_GetSetter_round_trip_value_type()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Age))!;
        var getter = AccessorCache.GetGetter(prop);
        var setter = AccessorCache.GetSetter(prop);

        var instance = new RefFixture();
        setter(instance, 42);

        getter(instance).Should().Be(42);
        instance.Age.Should().Be(42);
    }

    [Fact]
    public void GetGetter_and_GetSetter_round_trip_enum()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Day))!;
        var getter = AccessorCache.GetGetter(prop);
        var setter = AccessorCache.GetSetter(prop);

        var instance = new RefFixture();
        setter(instance, DayOfWeek.Wednesday);

        getter(instance).Should().Be(DayOfWeek.Wednesday);
    }

    [Fact]
    public void GetGetter_and_GetSetter_handle_nullable_value_type()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.OptionalId))!;
        var getter = AccessorCache.GetGetter(prop);
        var setter = AccessorCache.GetSetter(prop);

        var instance = new RefFixture();
        getter(instance).Should().BeNull();

        var id = Guid.NewGuid();
        setter(instance, id);
        getter(instance).Should().Be(id);

        setter(instance, null);
        getter(instance).Should().BeNull();
    }

    [Fact]
    public void GetGetter_returns_cached_delegate_instance_for_same_PropertyInfo()
    {
        // Reference equality matters here: if the cache misses, the migration loses its win.
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Age))!;

        var first = AccessorCache.GetGetter(prop);
        var second = AccessorCache.GetGetter(prop);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void GetSetter_returns_cached_delegate_instance_for_same_PropertyInfo()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Age))!;

        var first = AccessorCache.GetSetter(prop);
        var second = AccessorCache.GetSetter(prop);

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void GetGetter_throws_for_write_only_property()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.WriteOnly))!;

        Action act = () => AccessorCache.GetGetter(prop);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetSetter_throws_for_read_only_property()
    {
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.ReadOnly))!;

        Action act = () => AccessorCache.GetSetter(prop);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetGetter_throws_on_null_property()
    {
        Action act = () => AccessorCache.GetGetter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetSetter_throws_on_null_property()
    {
        Action act = () => AccessorCache.GetSetter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private class BaseFixture
    {
        public string? Inherited { get; set; }
    }

    private class DerivedFixture : BaseFixture
    {
        public int Own { get; set; }
    }

    [Fact]
    public void GetGetter_works_for_inherited_property()
    {
        // PropertyInfo.DeclaringType is the base type — verify the compiled getter
        // still works when the actual instance is a derived type.
        var prop = typeof(DerivedFixture).GetProperty(nameof(BaseFixture.Inherited))!;
        var getter = AccessorCache.GetGetter(prop);

        var instance = new DerivedFixture { Inherited = "from-base" };
        getter(instance).Should().Be("from-base");
    }

    [Fact]
    public void GetSetter_works_for_inherited_property()
    {
        var prop = typeof(DerivedFixture).GetProperty(nameof(BaseFixture.Inherited))!;
        var setter = AccessorCache.GetSetter(prop);

        var instance = new DerivedFixture();
        setter(instance, "written-via-base");

        instance.Inherited.Should().Be("written-via-base");
    }

    [Fact]
    public void Get_and_Set_share_no_cache_entries_for_same_property()
    {
        // Regression for the bug fixed in 2431b7e: getter and setter must use distinct
        // cache slots (we namespace with "get|" and "set|"). If they shared a slot,
        // resolving the setter after the getter (or vice versa) would either return
        // wrong-typed delegate or hit InvalidCastException.
        var prop = typeof(RefFixture).GetProperty(nameof(RefFixture.Age))!;

        var getter = AccessorCache.GetGetter(prop);
        var setter = AccessorCache.GetSetter(prop);

        var instance = new RefFixture();
        setter(instance, 7);
        getter(instance).Should().Be(7);
    }
}
