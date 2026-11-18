# MintPlayer.Spark.Webhooks.Receiver

Receive [Standard Webhooks](https://www.standardwebhooks.com/) from another site (for example a Spark site with
`MintPlayer.Spark.Apps`) in a MintPlayer.Spark app, durably.

1. Generate the event classes from the producer's catalog:

   ```
   spark webhooks generate --catalog https://producer.example/spark/apps/webhooks/catalog --lang cs --name Coverage --namespace MyApp.Webhooks -o Webhooks/Coverage.g.cs
   ```

2. Register the receiver (it needs Messaging):

   ```csharp
   builder.Services.AddSpark(builder.Configuration, spark =>
   {
       spark.AddMessaging();
       spark.AddWebhookReceiver("/webhooks/coverage", CoverageWebhooksProcessor.EventTypes,
           o => o.Secrets.Add(builder.Configuration["Coverage:WebhookSecret"]!));
   });
   ```

3. Handle the events you need:

   ```csharp
   public sealed partial class OnReportCompleted : IRecipient<WebhookMessage<ReportCompleted>>
   {
       public Task HandleAsync(WebhookMessage<ReportCompleted> message, CancellationToken cancellationToken) => ...;
   }
   ```

Each delivery is verified (401 otherwise), enqueued once per `webhook-id` (a retry or redelivery is not handled twice),
and answered 204. Your recipient runs afterwards with Messaging's retries. Events outside `EventTypes`, or that nobody
receives, go to `IRecipient<UnknownWebhookMessage>` when you have one, and are dropped otherwise.

Not a Spark app? Use `MintPlayer.Webhooks.Receiver.AspNetCore` and the generated `…WebhooksProcessor` directly.
