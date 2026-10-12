namespace MintPlayer.Spark.Queries;

/// <summary>
/// Extension methods for <see cref="CustomQueryArgs"/>.
/// </summary>
public static class CustomQueryArgsExtensions
{
    /// <summary>
    /// Validates that a parent is present and of the expected type.
    /// Throws <see cref="SparkQueryParentRequiredException"/> if the parent is missing or wrong type: the read paths
    /// answer it as a 400 (the request is incomplete), never a 500.
    /// </summary>
    public static void EnsureParent(this CustomQueryArgs args, string expectedTypeName)
    {
        if (args.Parent is null)
            throw new SparkQueryParentRequiredException(
                $"Custom query '{args.Query.Name}' requires a parent object.");
        if (!string.Equals(args.ParentType, expectedTypeName, StringComparison.OrdinalIgnoreCase))
            throw new SparkQueryParentRequiredException(
                $"Custom query '{args.Query.Name}' expects parent of type '{expectedTypeName}', got '{args.ParentType}'.");
    }

    /// <summary>
    /// Validates that a parent is present and one of the expected types.
    /// </summary>
    public static void EnsureParent(this CustomQueryArgs args, params string[] expectedTypeNames)
    {
        if (args.Parent is null)
            throw new SparkQueryParentRequiredException(
                $"Custom query '{args.Query.Name}' requires a parent object.");
        if (!expectedTypeNames.Any(t => string.Equals(args.ParentType, t, StringComparison.OrdinalIgnoreCase)))
            throw new SparkQueryParentRequiredException(
                $"Custom query '{args.Query.Name}' expects parent of type [{string.Join(", ", expectedTypeNames)}], got '{args.ParentType}'.");
    }
}

/// <summary>
/// A custom query that lists rows under a parent object ran without one (or under another type):
/// <see cref="CustomQueryArgsExtensions.EnsureParent(CustomQueryArgs, string)"/>. The request is incomplete, so the
/// query read paths answer 400 with this message (an agent's <c>query</c> tool says so too), after the query's
/// authorization. An <see cref="InvalidOperationException"/>, as it always was, for code that catches that.
/// </summary>
public sealed class SparkQueryParentRequiredException(string message) : InvalidOperationException(message);
