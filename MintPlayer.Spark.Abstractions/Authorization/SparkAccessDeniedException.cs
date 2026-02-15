namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Thrown when a user attempts an operation they are not authorized to perform.
/// Contains the resource string that was denied (e.g., "New/DemoApp.Person").
/// </summary>
public class SparkAccessDeniedException : Exception
{
    public string Resource { get; }

    public SparkAccessDeniedException(string resource)
        : base($"Access denied for resource: {resource}")
    {
        Resource = resource;
    }
}
