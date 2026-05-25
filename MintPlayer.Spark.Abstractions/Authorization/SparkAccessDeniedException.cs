namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Thrown when a user attempts an operation they are not authorized to perform
/// at the entity-type level (the principal lacks the action permission on the
/// type itself — e.g. "Edit/Person"). Maps to 401 unauthenticated / 403 forbidden.
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

/// <summary>
/// Thrown when the entity-type-level authorization passed but the Actions
/// class's row-level <c>IsAllowedAsync(action, entity)</c> hook denied the
/// specific instance. Endpoints map this to <c>404 Not Found</c> (M-3 / R2-H2):
/// the caller already knows the record exists from Read, so the existence
/// oracle isn't a new leak, but uniform 404 keeps the response shape consistent
/// with "you can't see this row" denials on other paths.
/// </summary>
public sealed class SparkRowLevelAccessDeniedException : SparkAccessDeniedException
{
    public SparkRowLevelAccessDeniedException(string resource) : base(resource) { }
}
