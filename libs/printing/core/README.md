# MintPlayer.Spark.Printing

Printing for MintPlayer.Spark (alias `printing`, `docs/warehouse_picking_PRD.md` §4.11, WM6). Generic: no warehouse or
commerce reference. Rendering (barcodes, ZPL, PDF, the label set) is `MintPlayer.Spark.Printing.Rendering`.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<AppUser>();   // the personal-access-token scheme the agents use (Q-W14)
    spark.AddPrinting();                  // Spark:Printing
});
```

```json
"Spark": { "Printing": {
  "Agents":   { "dock-1": { "UserId": "users/print-agent-dock-1" } },
  "Printers": {
    "dock-zebra": { "Adapter": "Agent", "Agent": "dock-1", "Address": "10.0.0.21:9100", "Format": "Zpl203",
                    "Site": "warehouse-sites/BRU", "Station": "pack-1", "LabelKinds": [ "Shipping", "Sscc" ] },
    "office":     { "Adapter": "Pdf", "Format": "PdfA4", "Default": true } } } }
```

- **Jobs** (`IPrintJobs`): `print-jobs/{key}`, the key being the caller's idempotency key. Content is a label template
  (rendered as ZPL at the printer's dpi, plus a PDF kept for the fallback), a PDF document, or a carrier's bytes
  (`application/pdf`, or `application/zpl` at a stated dpi). States: queued, delivered, printed, failed. A reprint is a new
  job. Finished jobs expire after `Retention` (30 days).
- **Routing** (`IPrintRouter`): station beats site beats any, a printer naming the kind beats one taking everything,
  otherwise the `Default` printer.
- **Agent endpoints** `/spark/printing/agent/{pull,ack,heartbeat,scale}`: bearer token of the agent principal only; an
  agent sees only its own printers' jobs. Delivery is at least once (`RedeliverAfter`), the agent's journal makes each job
  id print once, and acknowledgements are idempotent (W16).
- **Station endpoints** `/spark/printing/station/{queue,take,ack}` (needs `Read/PrintJob`): the PDF dialog and the
  bridges fetch their jobs; agent jobs that did not print within `FallbackAfter` (10 s), or whose agent missed its
  heartbeat for `AgentOfflineAfter`, are offered as PDF, and taking one withdraws it from the agent.
- **Startup refuses** a printer whose adapter is not registered (`Pdf` and `Agent` are built in; `AddPrintingQzTray()`,
  `AddPrintingZebraBrowserPrint()` add theirs), an agent printer without agent or `host:port`, ZPL on the PDF dialog.
- **Index**: `Printing_Jobs` (`[SparkIndexMap]`).

Bind `printing:operators` and `printing:administrators` in the application's `security.json`.
