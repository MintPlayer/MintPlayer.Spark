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
```

The AI dictionary is GS1's [Barcode Syntax Dictionary](https://github.com/gs1/gs1-syntax-dictionary), vendored in
`tools/gs1/` and turned into `pg/aitable.pg` by `node tools/gs1/generate-ai-table.mjs`.

Design and constraints: [docs/polyglot_migration_PRD.md](../../../docs/polyglot_migration_PRD.md).
