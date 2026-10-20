# MintPlayer.Spark.Gs1

GS1 logic for MintPlayer.Spark, written once in [Polyglot](https://github.com/MintPlayer/MintPlayer.Polyglot)
(`pg/*.pg`) and shared by .NET and `@mintplayer/ng-spark`.

- **.NET:** the C# is generated into `obj/` on every build (`PolyglotTargets=csharp`). It is `internal`, in its own
  namespace (`MintPlayer.Spark.Gs1.Core`), and the public API is the hand-written facade.
- **TypeScript:** the twin is committed in ng-spark's `scanning` entry point.

```csharp
Gs1CheckDigit.Compute("400638133393");   // 1
Gs1CheckDigit.IsValid("4006381333931");  // true

var scan = Gs1Barcode.Parse("]C10109521234543213\u001D10ABC123\u001D17251231");
// scan.IsValid, scan.Gtin == "09521234543213", scan.Lot == "ABC123", scan.Expiry == "251231"
Gs1Barcode.Parse("https://id.gs1.org/01/09521234543213/10/ABC123?17=251231");   // the same elements

Gs1Sscc.Create(0, "9521234", 1);         // "095212340000000013"

Gs1Gtin.Normalize("96385074");           // "00000096385074": registries store GTIN-14
LocationCheckDigits.Compute("A-01-02-3", siteSecret);   // two spoken digits for a location label

// SSCC allocation: one sequence per (extension digit, company prefix). ISsccSerialSource is the seam; the
// production source is Spark core's compare-exchange sequence (ISparkSequences, MintPlayer.Spark.Sequences).
var serialSource = new DelegateSsccSerialSource((key, ct) => new(sequences.NextAsync(key, 0, ct)));
var allocator = new SsccAllocator(serialSource, extensionDigit: 0, companyPrefix: "9521234");
var sscc = await allocator.AllocateAsync();
```

`LocationCheckDigits` and the facade helpers above are hand-written C#, not Polyglot: they are not GS1 parsing.

The AI dictionary is GS1's [Barcode Syntax Dictionary](https://github.com/gs1/gs1-syntax-dictionary), vendored in
`tools/gs1/` and turned into `pg/aitable.pg` by `node tools/gs1/generate-ai-table.mjs`.

Design and constraints: [docs/polyglot_migration_PRD.md](../../../docs/polyglot_migration_PRD.md).
