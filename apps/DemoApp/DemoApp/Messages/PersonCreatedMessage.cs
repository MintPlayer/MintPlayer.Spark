using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Messages;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);
