namespace SeoTester.Tests._Infrastructure;

/// <summary>A predicate over a whole object, reporting the object (a record prints its members) when it fails.</summary>
internal static class Satisfy
{
    public static void ShouldSatisfy<T>(this T value, Func<T, bool> predicate, string? because = null)
        => predicate(value).Should().BeTrue(because ?? $"{value} should satisfy the condition");
}
