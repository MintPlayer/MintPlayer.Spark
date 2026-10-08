namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Shows a value the server will never show again, in a dialog with a copy button: a generated client
/// secret, an API token, a recovery code (<c>docs/identity_provider_platform_PRD.md</c> D5).
/// </summary>
/// <remarks>
/// <para>
/// The value travels once, in the response to the action that created it. The server keeps only a
/// hash, so the dialog says so and asks the user to copy it now. It is never written to the page's
/// state, the URL or the browser's storage, and closing the dialog discards it.
/// </para>
/// <para>
/// A client without a handler for this type drops it silently (unknown operations are forward-
/// compatible), and the value is then lost. ng-spark handles it from its client-operations entry point.
/// </para>
/// </remarks>
public sealed class ShowSecretOperation : ClientOperation
{
    /// <summary>The dialog's title, in the request's language.</summary>
    public required string Title { get; init; }

    /// <summary>The explanation shown above the value, in the request's language.</summary>
    public required string Message { get; init; }

    /// <summary>The value itself.</summary>
    public required string Value { get; init; }
}
