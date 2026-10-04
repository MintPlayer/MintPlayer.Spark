namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown when a submitted action is disabled by the actions class's <c>OnDisableActionsAsync</c>
/// hook (#460, D13). Endpoint handlers translate it to <c>403</c> with the action name.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> a <see cref="Abstractions.Authorization.SparkAccessDeniedException"/>:
/// every endpoint maps that one to the M-3 refusal (404/401), which would hide the reason from a
/// caller who can plainly see the row. It is raised only after the row gate passed, so a 403 here
/// discloses nothing a 404 was protecting.
/// </remarks>
public sealed class SparkActionDisabledException : Exception
{
    /// <summary>The action that was refused, as the hook named it.</summary>
    public string ActionName { get; }

    public SparkActionDisabledException(string actionName)
        : base($"The action '{actionName}' is disabled.")
    {
        ActionName = actionName;
    }

    /// <summary>A refusal with its own message — a bulk call naming the rows that refused (#467, D18).</summary>
    public SparkActionDisabledException(string actionName, string message)
        : base(message)
    {
        ActionName = actionName;
    }
}
