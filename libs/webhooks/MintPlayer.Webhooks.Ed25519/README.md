# MintPlayer.Webhooks.Ed25519

Standard Webhooks `v2a` signatures: ed25519 over SHA-256 of `endpoint_id.msg_id.timestamp.payload`. The key pair
is shared by every endpoint, and receivers hold only the public key (`whpk_`).

```csharp
builder.Services.AddWebhookReceiver<MyWebhooks>(o => o.AddEd25519PublicKey("whpk_...", endpointId: "ep_..."));
```

Producer side: `new Ed25519WebhookSigner(WebhookSigningKey.Parse("whsk_..."))`. The cryptography is NSec
(libsodium), which is why this lives outside the `netstandard2.0` core.
