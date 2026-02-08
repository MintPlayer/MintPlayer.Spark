namespace MintPlayer.Spark.Messaging.Models;

public enum EMessageStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    DeadLettered
}
