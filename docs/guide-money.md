# Money

Prices, fees, discounts and refunds are `Money`: an amount in one ISO 4217 currency, exact to the
currency's minor unit. It lives in `MintPlayer.Spark.Model` (namespace `MintPlayer.Spark.Abstractions`),
so an entity library can use it without ASP.NET Core. Background: commerce PRD §5 X4, rounding spike S21.

```csharp
public class Product
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public Money? Price { get; set; }
}
```

That is all an entity needs. Model sync gives the attribute `"dataType": "Money"` and
`"renderer": "money"`, the client shows `€ 12,30` (in the viewer's locale) and edits it as an amount
plus a currency, and a query grid sorts and filters it.

## The value

```csharp
var unit  = new Money(19.99m, "EUR");          // or Money.FromMinorUnits(1999, "EUR")
var line  = unit * 3;                          // 59.97 EUR, exact
var total = line + new Money(4.95m, "EUR");    // 64.92 EUR
total.ToMinorUnits();                          // 6492
total.Amount;                                  // 64.92m, always with the currency's decimals
Money.MinorUnitDigits("JPY");                  // 0 — KWD is 3, CLF is 4
```

- **Exact by construction.** `new Money(12.345m, "EUR")` throws: EUR has two decimals, and Money never
  rounds silently. Trailing zeros are fine (`12.3400m` is 12.34).
- **The currency is an ISO 4217 code** with minor units, case-insensitive, stored upper-case. Unknown
  codes, and codes that are not money a shop charges (`XAU`, `XDR`, `XTS`, …), throw.
- **Currencies never mix.** `+`, `-`, `<`, `>` and `Allocate` on two currencies throw
  `CurrencyMismatchException`. There is no implicit conversion. `Money.Sum(values, "EUR")` sums a list
  (zero when it is empty).
- **`CompareTo` is the sort order**, currency first and then amount, and never throws: a grid holding
  several currencies still sorts. The comparison operators are the ones that refuse to mix.
- `ToString()` is invariant, `12.30 EUR`, for logs. Display formatting belongs to the client.

## Rounding: two primitives, nothing else

Every rounding decision goes through one of these, applied once per quotient:

| Primitive | Does | Example |
|---|---|---|
| `Money.RoundDiv(n, d)` | `n / d` rounded **half away from zero** (2.5 → 3, −2.5 → −3) | 21% VAT on a net of 1999 c: `RoundDiv(1999 * 21, 100)` = 420 |
| `money.Share(n, d)` | `money × n / d`, rounded once with `RoundDiv` | 15% off: `price.Share(15, 100)`; VAT in a 21% gross: `gross.Share(21, 121)` |
| `Money.Allocate(total, weights)` | splits `total` so the parts **sum exactly**, each within one unit of its pro-rata share (largest remainder) | a discount over order lines: `discount.Allocate(lineAmounts)` |

`Allocate` gives the leftover units to the largest remainders, ties to the larger weight and then the
earlier index, so the split is deterministic. A zero weight gets nothing; when the total does not exceed
the sum of the weights, no part exceeds its weight (a discount can never make a line negative). A
negative total is split as its absolute value and negated, so a refund's split mirrors the charge's.

There is deliberately **no `Money × decimal`**: a percentage is a ratio of integers (`Share(1250,
10_000)` for 12.5%), so the one rounding step is visible in the code.

### The order rules built on them

Spike S21 settled these for invoicing (M10), and checked them on 10,000 generated orders × 4
configurations: every total, VAT base and credit note reconciles to the cent.
`MoneyRoundingPropertyTests` keeps that run as a seeded test (about a second).

- A line is `unit × qty − line discount`; a percentage line discount is rounded once, on the line.
- An order discount is rounded once on the discountable base (fees are not discountable) and allocated
  over the discountable lines by `Allocate`.
- VAT is rounded once per rate group and split over the lines by `Allocate` (the EN 16931 BR-CO-17
  shape), or per line. Net pricing with VAT per rate group meets BR-CO-17 exactly, so it is the B2B
  default; gross pricing (B2C) keeps the customer price fixed.
- A refund by units is `round(lineGross × unitsReturned / qty)` cumulatively; a refund by amount is
  allocated over what each line can still refund. Credit notes mirror the invoice lines with
  cumulative-difference VAT, so a full refund cancels the invoice exactly.

## Stored and wire shape

One shape everywhere, the API (System.Text.Json), RavenDB (Newtonsoft) and the client:

```json
{ "Amount": 12.30, "Currency": "EUR" }
```

The member names are the CLR names **whatever the naming policy**, like `TranslatedString`: RavenDB's
LINQ translator addresses stored JSON by CLR member name, so `x.Price.Amount` in a query or an index map
only works when the stored names are exactly these. Reading is case-insensitive and also accepts the
amount as a string (`"12.30"`). A value that is not exact money (too many decimals, unknown currency) is
refused: the save answers **400** naming the attribute.

## In queries

A Money column sorts by **currency, then amount**, and a filter matches currency **and** amount.

- **Collection queries** (no index) need nothing: the executor addresses `Price.Currency` and
  `Price.Amount`, which RavenDB's auto-index covers.
- **Static indexes** cannot be addressed by a nested path. An index that serves a Money column emits two
  flat **companions**, `{Name}Amount` and `{Name}Currency` (`SparkMoney.AmountCompanion` /
  `CurrencyCompanion`), declared `[IgnoreProperty]` on the projection like a `{Name}Sort` companion,
  and keeps the Money itself stored but not indexed:

  ```csharp
  public class ProductsByPrice : AbstractIndexCreationTask<Product, ProductsByPrice.Row>
  {
      public class Row
      {
          public string? Name { get; set; }
          public Money? Price { get; set; }
          [IgnoreProperty] public decimal? PriceAmount { get; set; }
          [IgnoreProperty] public string? PriceCurrency { get; set; }
      }

      public ProductsByPrice()
      {
          Map = products => from p in products select new Row
          {
              Name = p.Name,
              Price = p.Price,
              PriceAmount = p.Price!.Amount,
              PriceCurrency = p.Price!.Currency,
          };
          Index(r => r.Price, FieldIndexing.No);
          StoreAllFields(FieldStorage.Yes);
      }
  }
  ```

  `[GenerateIndex]` emits both companions for you. A static index without them reports the column as
  not sortable and not filterable rather than failing the query.

## On the client

The core `money` renderer (ng-spark, no registration) uses the platform, no library:

- **Display**: `Intl.NumberFormat` with `style: 'currency'`, in the viewer's locale (the browser's
  regional variant of the UI language, `nl-BE` on a Dutch UI in Belgium). An amount that carries more
  decimals than CLDR shows for its currency is shown in full, never rounded on screen.
  `"rendererOptions": { "locale": "nl-BE" }` pins the locale for a column.
- **Editing**: an `<input type="number">` (the browser reads the viewer's decimal separator) beside a
  currency picker filled from `Intl.supportedValuesOf('currency')` and named with
  `Intl.DisplayNames`. An amount with more decimals than the currency allows is marked invalid; the
  server refuses it on save.

  ```json
  { "name": "Price", "dataType": "Money", "renderer": "money",
    "rendererOptions": { "currencies": ["EUR", "USD"], "currency": "EUR" } }
  ```

  `currencies` limits the picker (a single code shows as a fixed label); `currency` is where a new value
  starts.
- **Helpers** in `@mintplayer/ng-spark/models`: `Money`, `asMoney`, `formatMoney`, `viewerLocale`,
  `currencyFractionDigits`, `currencyChoices`, `currencyDisplayName`.

Browser support: `Intl.NumberFormat` currency formatting and `Intl.DisplayNames` are in every evergreen
browser (Chrome 81, Firefox 86, Safari 14.1). `Intl.supportedValuesOf` arrived in Chrome 99, Firefox
93 and Safari 15.4; where it is missing, the picker offers the configured currencies and the value's own.
