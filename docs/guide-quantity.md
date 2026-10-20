# Quantity

Stock on hand, pick quantities, pack sizes and weights are `Quantity`: an amount in one unit (`EA`, `CS`,
`PAL`, `KG`, or a UN/ECE Recommendation 20 code such as `C62`). It is built on the same value-type
infrastructure as [Money](guide-money.md) and lives beside it in `MintPlayer.Spark.Model` (namespace
`MintPlayer.Spark.Abstractions`). Background: warehouse PRD §4.5 "Units" and §5 WX8.

```csharp
public class StockLine
{
    public string? Id { get; set; }
    public string Sku { get; set; } = "";
    public Quantity? OnHand { get; set; }
}
```

Model sync gives the attribute `"dataType": "Quantity"` and `"renderer": "quantity"`; the client shows
`1,234.5 EA` in the viewer's locale and edits it as an amount plus a unit; a query grid sorts and filters it.

## The value

- **Exact.** The amount is a `decimal`; `+`, `-` and `× factor` never round. Trailing zeros carry no
  meaning and are dropped (`12.0 EA` is stored as `12 EA`).
- **Units never mix.** Adding, subtracting or comparing (`<`, `>`) quantities in different units throws
  `UnitMismatchException`. `Equals` is strict: `1 CS` is not `12 EA`. `CompareTo` is the grid's total
  order (unit, then amount) and never throws.
- **Unit codes** are 1 to 16 letters, digits, `_` or `-`, case-insensitive, stored upper-case.
  `Quantity.IsValidUnit` checks one.

## Packaging levels

Stock is stored in base units; packaging levels convert at input and display. `PackagingLevels` is an
item's conversion table:

```csharp
var levels = new PackagingLevels("EA", [new("CS", 12), new("PAL", 480)]);

levels.ToBase(new Quantity(2, "CS"));               // 24 EA: scanning a case barcode books its pack quantity
levels.Convert(new Quantity(27, "EA"), "CS");       // 2.25 CS
levels.Breakdown(new Quantity(507, "EA"));          // [1 PAL, 2 CS, 3 EA]
levels.AreEqual(new Quantity(1, "CS"), new Quantity(12, "EA"));  // true
levels.Sum([new Quantity(2, "CS"), new Quantity(3, "EA")]);      // 27 EA
```

A conversion is exact or refused: 1 EA in a pack of 3 is not an exact number of packs, so `Convert`
throws `QuantityConversionException` (`TryConvert` answers `false`). A unit the table does not know
throws the same exception. A level may be smaller than the base unit (`new("G", 0.001m)` under `KG`).

## Stored and wire shape

One shape for the API (System.Text.Json, `QuantityJsonConverter`), RavenDB (Newtonsoft,
`QuantityNewtonsoftJsonConverter`) and the client, with the CLR member names whatever the naming policy:

```json
{ "Amount": 12.5, "Unit": "EA" }
```

Reading is case-insensitive and accepts the amount as a string. An invalid unit on save is a 400 naming
the attribute. The TypeScript type is hand-written in `@mintplayer/ng-spark/models` (`quantity.ts`) and
declared to the contract tool with `[assembly: SparkClientType(typeof(Quantity), "Quantity", "quantity")]`.

## In queries

Exactly as for Money: a Quantity column sorts by **unit, then amount**, and a filter matches unit **and**
amount. Collection queries address `OnHand.Unit` / `OnHand.Amount` directly. A static index emits two
flat companions, `{Name}Amount` and `{Name}Unit` (`SparkQuantity.AmountCompanion` / `UnitCompanion`),
declared `[IgnoreProperty]` on the projection, and keeps the Quantity stored but not indexed.
`[GenerateIndex]` emits both for you.

## On the client

The core `quantity` renderer (ng-spark, no registration):

- **Display**: `Intl.NumberFormat` in the viewer's locale, then the unit code. With
  `baseUnit` and `levels` it shows whole packages, `2 CS + 3 EA`.
- **Editing**: an `<input type="number">` beside a unit picker. The value is sent in the unit picked;
  converting to the base unit is the server's job.

  ```json
  { "name": "OnHand", "dataType": "Quantity", "renderer": "quantity",
    "rendererOptions": { "baseUnit": "EA", "levels": [{ "unit": "CS", "baseQuantity": 12 }], "unit": "EA" } }
  ```

  `units` lists the codes to offer (a single code shows as a fixed label); without it the picker offers
  `baseUnit` and every level. `unit` is where a new value starts; `locale` pins the display locale.
- **Helpers** in `@mintplayer/ng-spark/models`: `Quantity`, `PackagingLevel`, `asQuantity`,
  `formatQuantity`, `breakdownQuantity`, `formatQuantityBreakdown`, `asPackagingLevels`.
