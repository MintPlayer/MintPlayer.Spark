using MintPlayer.Spark.Messaging.Abstractions;

namespace DemoApp.Messages;

[MessageQueue("PersonEvents")]
public record PersonDeletedMessage(string PersonId);
