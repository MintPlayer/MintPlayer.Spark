namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// "The user chose not to; do nothing." Throw it from a before-save or before-delete hook (#482),
/// typically after a <c>Retry.Action</c> prompt was answered with Cancel.
/// </summary>
/// <remarks>
/// <para>
/// Not an error. The framework writes nothing, takes back everything the hooks put in the request
/// session, runs no after-hook and no replication, logs nothing above Debug, and answers as a no-op
/// success: a delete or delete-many 204, an update 200 with the object as stored, a create 204.
/// </para>
/// <para>
/// A bulk delete is all or nothing (#460, D18): one row's cancel cancels the whole batch.
/// </para>
/// </remarks>
public sealed class SparkCancelException : Exception
{
    public SparkCancelException()
        : base("The write was cancelled by a hook.")
    {
    }
}
