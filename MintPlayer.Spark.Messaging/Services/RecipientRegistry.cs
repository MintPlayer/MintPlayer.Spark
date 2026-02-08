namespace MintPlayer.Spark.Messaging.Services;

public class RecipientRegistry
{
    private readonly Dictionary<Type, List<Type>> _mappings = new();

    public void Register(Type messageType, Type recipientType)
    {
        if (!_mappings.TryGetValue(messageType, out var recipients))
        {
            recipients = new List<Type>();
            _mappings[messageType] = recipients;
        }

        if (!recipients.Contains(recipientType))
        {
            recipients.Add(recipientType);
        }
    }

    public IReadOnlyList<Type> GetRecipientTypes(Type messageType)
    {
        return _mappings.TryGetValue(messageType, out var recipients)
            ? recipients
            : Array.Empty<Type>();
    }
}
