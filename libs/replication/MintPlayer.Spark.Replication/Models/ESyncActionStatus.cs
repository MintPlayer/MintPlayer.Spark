namespace MintPlayer.Spark.Replication.Models;

/// <summary>Where a write-back request is (F12, F14).</summary>
public enum ESyncActionStatus
{
    /// <summary>Waiting to be sent, or parked for a retry.</summary>
    Pending,

    /// <summary>Being sent.</summary>
    Processing,

    /// <summary>The owner applied it (or had applied it, or a newer edit superseded it).</summary>
    Completed,

    /// <summary>The owner refused it, finally, with a reason. Never retried; the initiator is told.</summary>
    Refused,

    /// <summary>The retry budget ran out. Can be re-driven.</summary>
    Failed,
}
