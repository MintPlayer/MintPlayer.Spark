namespace MintPlayer.Spark.Messaging.Abstractions;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class MessageQueueAttribute : Attribute
{
    public string QueueName { get; }

    public MessageQueueAttribute(string queueName)
    {
        QueueName = queueName;
    }
}
