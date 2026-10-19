# MintPlayer.Spark.Commerce.Invoicing.Peppol.Storecove

Storecove as Peppol access point for `MintPlayer.Spark.Commerce.Invoicing` (commerce PRD §4.8, D19, Q-O2).

```csharp
spark.AddCommerceInvoicing().UseStorecovePeppol();
```

```json
"Spark": { "Commerce": { "Invoicing": { "Peppol": { "Provider": "storecove", "Storecove": {
  "LegalEntityId": 123456,
  "LegalEntities": { "Organizations/42": 234567 }
} } } } }
```

The API key goes in user-secrets: `Spark:Commerce:Invoicing:Peppol:Storecove:ApiKey`. It is sent only to the HTTPS base URL and
never logged.

| What | Storecove API (v2) |
|---|---|
| Send | `POST document_submissions`: `legalEntityId`, `idempotencyGuid`, `routing.eIdentifiers[{scheme,id}]`, `document.documentType = invoice`, `document.rawDocumentData{document (base64 UBL), parse, parseStrategy = ubl, processId, documentTypeId}`; answers `{ "guid": … }` |
| Receiver check | `POST discovery/receives`: `documentTypes`, `network = peppol`, `metaScheme = iso6523-actorid-upis`, `scheme`, `identifier`; answers `{ "code": "OK" }` when the receiver can receive |
| Schemes | ICD → Storecove scheme map in `StorecoveAccessPoint.Schemes` (`0208` → `BE:EN`, `9925` → `BE:VAT`, …) |

⚠️ **Doc-derived** (round 4, D37): written against https://www.storecove.com/docs/ (read 2026-10-19). The submission's
`rawDocumentData` field set, `idempotencyGuid` and the scheme map were not replayed against Storecove's sandbox; check them with
a sandbox key before production. One legal entity per issuer: in connected mode, each seller's Storecove legal entity id.
Status updates (Storecove webhooks / evidence) are not consumed yet: a submission accepted by Storecove is recorded as `Sent`.
