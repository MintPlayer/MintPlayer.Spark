using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Retry;

public sealed class RetryResult
{
    /// <summary>
    /// The <see cref="Option"/> of a cancelled step: the client's own Cancel button or a dismissed modal
    /// on a <c>cancellable</c> prompt (<see cref="IRetryAccessor.Action"/>), and a client-method step
    /// whose method was unknown, unsupported, rejected or threw. An identifier, never a label: the
    /// button the user sees is the client's translation of <c>common.cancel</c>.
    /// </summary>
    public const string CancelOption = "Cancel";

    /// <summary>
    /// The option the user clicked, exactly as the action offered it, or <see cref="CancelOption"/>.
    /// For a client-method step (<see cref="IRetryAccessor.Invoke"/>) <c>"OK"</c> when it answered.
    /// </summary>
    public required string Option { get; init; }

    /// <summary>
    /// The step index this result corresponds to (0-based).
    /// Managed automatically by the framework.
    /// </summary>
    public int Step { get; init; }

    /// <summary>
    /// The PersistentObject with attribute values as filled in by the user.
    /// Null if no PersistentObject was shown in the modal.
    /// </summary>
    public PersistentObject? PersistentObject { get; init; }

    /// <summary>
    /// What the client method of an <see cref="IRetryAccessor.Invoke"/> step resolved with. Null for
    /// an ordinary prompt, and for a cancelled one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Browser input like any request body: validate it before acting on it. The client method only
    /// shapes the answer; it vouches for nothing.
    /// </remarks>
    public JsonElement? Value { get; init; }
}
