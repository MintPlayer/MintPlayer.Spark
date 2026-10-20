# MintPlayer.Spark.Printing.QzTray

The QZ Tray adapter for MintPlayer.Spark.Printing (`docs/warehouse_picking_PRD.md` §4.11). QZ Tray (LGPL-2.1) runs on the
station and prints silently when its messages are signed by the site's certificate.

```csharp
spark.AddPrintingQzTray();   // also adds AddPrinting()
```

```json
"Spark": { "Printing": {
  "Printers": { "pack-1-zebra": { "Adapter": "QzTray", "DeviceName": "ZDesigner GK420d", "Format": "Zpl203", "Station": "pack-1" } },
  "QzTray": { "CertificatePath": "certs/qz-public.pem", "Algorithm": "SHA512" } } }
```

Keep the **private key** out of the repository: `dotnet user-secrets set "Spark:Printing:QzTray:PrivateKey" "<PEM>"` or a
vault. Startup refuses a missing certificate or a key that does not parse (the value is never logged).

Endpoints (signed-in operators with `Read/PrintJob`): `GET /spark/printing/qz-tray/certificate`,
`POST /spark/printing/qz-tray/sign` (`{ "message": "…" }` → `{ "signature", "algorithm" }`). The browser side is
`@mintplayer/ng-spark/printing` (WM9).
