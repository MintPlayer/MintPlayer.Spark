using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Blocking operation — the action cannot proceed without an answer. The frontend either opens a
/// retry modal and the user selects an option, or, when <see cref="ClientMethod"/> is set, runs the
/// client method the app registered under that name; either way the original request is resubmitted
/// with the accumulated <c>retryResults[]</c>. Pushed by <c>IRetryAccessor.Action(...)</c> /
/// <c>IRetryAccessor.Invoke(...)</c> before it throws.
/// </summary>
public sealed class RetryOperation : ClientOperation
{
    public required int Step { get; init; }
    public required string Title { get; init; }
    public required string[] Options { get; init; }
    public string? DefaultOption { get; init; }
    public PersistentObject? PersistentObject { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Whether the client offers a Cancel of its own beside <see cref="Options"/>, labelled in the
    /// user's language, and answers it, or a dismissed modal, with <c>RetryResult.CancelOption</c>.
    /// When false, a dismissal abandons the request instead of answering.
    /// </summary>
    public bool Cancellable { get; init; }

    /// <summary>
    /// The name of a client method (<c>provideSparkClientMethods</c>) to run instead of showing a
    /// modal, for example <c>"webauthn.create"</c>. Null for an ordinary prompt.
    /// </summary>
    /// <remarks>
    /// Fails closed: the client only runs a method it registered under this name, and answers
    /// <c>"Cancel"</c> for an unknown name, a rejection or a thrown error. Nothing is evaluated.
    /// </remarks>
    public string? ClientMethod { get; init; }

    /// <summary>The argument handed to <see cref="ClientMethod"/>, already serialized. Null when there is none.</summary>
    public JsonElement? Arguments { get; init; }
}
