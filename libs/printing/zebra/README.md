# MintPlayer.Spark.Printing.ZebraBrowserPrint

The Zebra Browser Print adapter for MintPlayer.Spark.Printing (`docs/warehouse_picking_PRD.md` §4.11, Q-W15): USB and
network printers on Windows and macOS, network and Bluetooth on Android, through Zebra's local service.

```csharp
spark.AddPrintingZebraBrowserPrint();   // also adds AddPrinting()
```

```json
"Spark": { "Printing": {
  "Printers": { "pack-2-zebra": { "Adapter": "ZebraBrowserPrint", "DeviceName": "ZD421", "Format": "Zpl203", "Station": "pack-2" } },
  "ZebraBrowserPrint": { "LibraryUrl": "/vendor/zebra/BrowserPrint-3.1.250.min.js" } } }
```

**The library is never bundled** (Q-W15): Zebra provides it on request and its licence is unverified. The host obtains it,
serves it, and names its URL (https or an app path); check the licence before release. `GET /spark/printing/zebra/settings`
gives the station's browser that URL; `@mintplayer/ng-spark/printing` (WM9) loads it and sends the station's jobs.
