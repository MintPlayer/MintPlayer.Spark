using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Library.Messages;

[MessageQueue("PersonEvents")]
public record PersonCreatedMessage(string PersonId, string FullName);
