# MintPlayer.Spark.Printing.Rendering

Document and label rendering (WX6, `docs/warehouse_picking_PRD.md` §4.11), shared by the warehouse and commerce. No Spark,
no RavenDB, one dependency (QRCoder, MIT, for QR only).

- **Barcodes** (`Barcodes/`): Code 128 (subsets B and C), GS1-128 (FNC1, separators after variable-length AIs), EAN-13,
  Data Matrix ECC 200 (10×10 to 48×48, ASCII encodation, GS1 DataMatrix), QR. Pure encoders: symbol values and modules.
- **PDF** (`Pdf/PdfWriter`): a dependency-free PDF 1.4 writer: any page size (A4, A6, 4×6 in), Helvetica text, lines,
  rectangles, vector barcodes, JPEG. The W17 choice (PRD §13): no third-party PDF library.
- **ZPL** (`Zpl/ZplCanvas`): ZPL II at 203 or 300 dpi with the printer's own barcode commands (`^BC` fed the same symbol
  values through invocation codes, `^BE`, `^BX`, `^BQ`).
- **Labels** (`Labels/`): templates that draw once on an `ILabelCanvas` and render to ZPL or PDF (`LabelRenderer`):
  `LocationLabel`, `ItemLabel` (items and cases, GS1-128 when a GTIN is known), `ToteLabel`, `SsccLabel` (the GS1
  Logistic Label).
- **Documents** (`Documents/`): A4 `PackingSlip` and `PickList`, multi-page.

```csharp
var zpl = LabelRenderer.Zpl(new LocationLabel("A-01-03-B", "47"), dpi: 300);
var pdf = LabelRenderer.Pdf(new SsccLabel("376123450000000017", from, to));
```

Tests compare generated ZPL text and PDF content offline; Labelary is for manual previews only, never in CI.
