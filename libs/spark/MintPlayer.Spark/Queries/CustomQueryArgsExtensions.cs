namespace MintPlayer.Spark.Queries;

/// <summary>
/// Extension methods for <see cref="CustomQueryArgs"/>.
/// </summary>
public static class CustomQueryArgsExtensions
{
    /// <summary>
    /// Validates that a parent is present and of the expected type.
    /// Throws <see cref="InvalidOperationException"/> if the parent is missing or wrong type.
    /// </summary>
    public static void EnsureParent(this CustomQueryArgs args, string expectedTypeName)
    {
        if (args.Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{args.Query.Name}' requires a parent object.");
        if (!string.Equals(args.ParentType, expectedTypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Custom query '{args.Query.Name}' expects parent of type '{expectedTypeName}', got '{args.ParentType}'.");
    }

    /// <summary>
    /// Validates that a parent is present and one of the expected types.
    /// </summary>
    public static void EnsureParent(this CustomQueryArgs args, params string[] expectedTypeNames)
    {
        if (args.Parent is null)
            throw new InvalidOperationException(
                $"Custom query '{args.Query.Name}' requires a parent object.");
        if (!expectedTypeNames.Any(t => string.Equals(args.ParentType, t, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Custom query '{args.Query.Name}' expects parent of type [{string.Join(", ", expectedTypeNames)}], got '{args.ParentType}'.");
    }
}
