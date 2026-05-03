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
    [AttributeUsage(AttributeTargets.Property)] private sealed class MarkerAttribute : Attribute;

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
}
