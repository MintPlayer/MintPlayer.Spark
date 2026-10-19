namespace MintPlayer.Spark.Commerce.Tests;

/// <summary>Returns the thrown exception for further assertions (xunit's <c>Assert</c> is banned here; RS0030).</summary>
internal static class TestThrows
{
    public static async Task<T> ThrowsAsync<T>(Func<Task> act) where T : Exception
        => (await act.Should().ThrowAsync<T>()).Which;

    public static T Throws<T>(Action act) where T : Exception
        => act.Should().Throw<T>().Which;
}
