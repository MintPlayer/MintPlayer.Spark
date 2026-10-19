# MintPlayer.Spark.Commerce.Invoicing.Peppol.Billit

Billit as Peppol access point for `MintPlayer.Spark.Commerce.Invoicing` (commerce PRD §4.8, D19, Q-O2). Billit is a Belgian
access point; Belgium made B2B e-invoicing over Peppol mandatory on 2026-01-01.

```csharp
spark.AddCommerceInvoicing().UseBillitPeppol();
```

```json
"Spark": { "Commerce": { "Invoicing": { "Peppol": { "Provider": "billit", "Billit": {
  "PartyId": "123456",
  "Parties": { "Organizations/42": "234567" },
  "ApiBaseUrl": "https://api.sandbox.billit.be/"
} } } } }
```

The API key goes in user-secrets (`Spark:Commerce:Invoicing:Peppol:Billit:ApiKey`); it is sent only over HTTPS and never logged.

| What | Billit API |
|---|---|
| Send | `POST v1/peppol/sendxml`, JSON `{ "XML": "<?xml …" }`, headers party + key; 200 answers the `InboxItemID` |
| Receiver check | `GET v1/peppol/participantInformation/{id}` (404: not registered) |

⚠️ **Doc-derived** (round 4, D37), from https://docs.billit.be/docs/send-ubl-to-peppol-1 (read 2026-10-19). Confirmed there: the
path, the sandbox and production hosts, the `XML` field, the 200 + `InboxItemID` answer and that Billit checks the Peppol
validation rules on submission. **Not confirmed:** the header names (`ApiKey`, `PartyID`; the page refers to Billit's
authentication page) and the participant-information endpoint. Check both with a sandbox key before production.
