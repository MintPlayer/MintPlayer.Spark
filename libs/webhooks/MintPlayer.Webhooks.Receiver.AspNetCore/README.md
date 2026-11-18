# MintPlayer.Webhooks.Receiver.AspNetCore

Receive Standard Webhooks in ASP.NET Core in two lines:

```csharp
builder.Services.AddWebhookReceiver<CoverageWebhooks>(o => o.Secrets.Add(builder.Configuration["Coverage:WebhookSecret"]!));
app.MapWebhookReceiver<CoverageWebhooks>("/webhooks/coverage");

public sealed class CoverageWebhooks : WebhookProcessor
{
    protected override Task OnEventAsync(WebhookContext context, CancellationToken ct) => ...;
}
```

The endpoint answers 204, 400 (not a webhook envelope), 401 (bad signature or timestamp) or 413 (body over
`MaxBodyBytes`). An exception from the processor becomes a 500, and the producer retries.
