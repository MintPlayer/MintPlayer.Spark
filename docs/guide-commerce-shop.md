# Commerce: the shop

`MintPlayer.Spark.Commerce.Shop` (alias `commerce-shop`) sells goods, digital content and services through Checkout:
products with variants, categories, typo-tolerant search with facet counts, the checkout delivery step over Shipping,
Paid → Shipped → Delivered, returns, verified-purchase reviews and product feeds. Angular:
`@mintplayer/ng-spark/commerce/shop`. Plan and decisions: [`commerce_PRD.md`](commerce_PRD.md) §4.10, D44.

> **Legal citations.** Every legal rule below names its article. The ones marked *checked* were read against the
> EUR-Lex text on 2026-10-20 (D37; the compliance table in [guide-commerce-compliance.md](guide-commerce-compliance.md)); national transpositions were not, so check them where a member state has an
> option. Defaults are not legal advice.

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();                      // or another provider
    spark.AddCommerceShipping().UseSendcloud();     // or another carrier adapter
    spark.AddCommerceShop();                        // Spark:Commerce:Shop; adds Checkout and Shipping
});
```

```jsonc
"Spark": {
  "Commerce": {
    "Channels": { "shop": { "Allocation": "HoldFromSelection" } },
    "Shop": {
      "DefaultChannel": "shop",
      "ProductPagePath": "/shop/products/{slug}",
      "Fulfilment": "ShopOnly",              // or Warehouse (D13)
      "LegalGuaranteeMonths": 24,           // at least 24, Directive (EU) 2019/771 Art. 10(1)
      "GuaranteeNotice": null,              // { "en": "…", "nl": "…" } to replace the library's text
      "Returns": { "AutoReturnLabel": true, "ReceiptOn": "CarrierDelivery" },
      "Reviews": { "Enabled": true, "PreModeration": true, "Window": "365.00:00:00" },
      "Feeds": { "Enabled": true, "CacheDuration": "01:00:00" }
    }
  }
}
```

Startup refuses a default channel that is not configured, page paths without `{slug}`, a guarantee period under 24
months and other impossible values. The delivery choices expire through RavenDB's document expiration: enable it on the
database, or the expired choices stay (they are never used after `ExpiresAt`).

## Catalogue

- **Products** (`ShopProduct`) are owned by the seller's organization (X8): its members edit them; publishing needs the
  organization's `Publish` role (Admin). **Variants** (`ShopVariant`) are their own documents: SKU (unique per seller), a
  GTIN with a valid GS1 check digit (GS1 General Specifications §7.9), their own price or the product's, their own quota
  (`Capacity` in ShopOnly mode, the warehouse's stock feed in Warehouse mode through `ISparkShopStock`). Item ids are
  `shop:{variantId}`.
- **Categories** (`ShopCategory`) are the platform's; only `ManageSellers/Commerce` edits them.
- **Search** (`GET /spark/commerce/shop/catalog`) runs on the declared index `ShopProducts/Catalog` (Lucene, typo-tolerant,
  D38). Facet counts come from the same index map (brand, declared attributes, variant options, price ranges), never from
  distinct values (GB13). Ranking (GA5, CRD Art. 6a(1)(a) and P2B Art. 5; *checked*): relevance with search words,
  else newest first; the buyer may sort by price or rating; no paid placement.
- **Publication is refused** while the product cannot sell: no active variant, an invalid SKU, GTIN, HS code or country,
  an unknown channel, the seller gate (D21, D43), a missing legal document (GA2), an image without alternative text
  (WCAG 2.1 SC 1.1.1), and for goods the GPSR data below.

### Unit price (GA3)

A product sold by quantity names its unit of measure (kg, l, m, m², m³, piece) and net quantity (product or variant).
The page shows the unit price beside the FinalPrice, **computed, never typed**: FinalPrice ÷ net quantity × the reference
quantity (1 by default; 0.1 for "per 100 g" where a member state allows it), rounded once to the minor unit. Basis: Price
Indication Directive 98/6/EC Art. 2(b) and 3(1) (*checked*). Publication is refused without a net quantity.

### Product safety (GA7)

The `ProductSafety` attribute group carries what Reg. (EU) 2023/988 (GPSR) Art. 19 requires an online offer to show
(*checked*): the manufacturer's name, postal and electronic address (19(a)); the EU responsible person when the
manufacturer is established outside the EU (19(b), with Art. 16 and Reg. (EU) 2019/1020 Art. 4); a product identifier
and a picture (19(c)); warnings and safety information per language (19(d)). A goods product without it **cannot be
published** (refused at publication, never at startup). The product page shows it in a fixed block. Northern Ireland
(`XI`) follows EU product rules under the Windsor Framework; list it yourself if you need to (*not checked*: the Windsor Framework text was not read).

### Guarantees (GA8)

- The **legal-guarantee notice** (Directive (EU) 2019/771 Art. 10(1): at least two years from delivery, longer where a
  member state says so, Art. 10(3); *checked*) is shown on every goods product page and attached to the order
  confirmation as `guarantee-information.html` (CRD Art. 6(1)(l) as amended by Directive (EU) 2024/825, applying from 27
  September 2026; *checked*). ⚠️ The **harmonised notice** of CRD Art. 22a is published: Commission Implementing
  Regulation (EU) 2025/1960, applying from 27 September 2026, and "none of the elements of the harmonised notice can be
  edited". The library's default wording is **not** that notice: replace it through `GuaranteeNotice` (*checked*).
- A producer's **commercial guarantee of durability** (CRD Art. 6(1)(la), inserted by Directive (EU) 2024/825: a
  guarantee "at no additional cost, covering the entire good and with a duration of more than two years", shown with
  the harmonised label of Regulation (EU) 2025/1960; *checked*) is a field on the
  product (months, guarantor, terms), shown on the page and in the same attachment.
- Seller texts are not scanned for generic environmental claims; the ban in Directive 2005/29/EC Annex I as amended by
  2024/825 is the seller's matter.

### Customs, age, media

- **HS code and country of origin** per product, overridable per variant (GB6). Shipping builds the CN22/CN23 or the
  commercial invoice from them; a shipment leaving the EU customs territory with incomplete lines is refused by Shipping.
- **`MinimumAge`** (GB12): Checkout asks the buyer to attest the age; the confirmation and the handover carry an ID check.
- **Galleries** with images per variant and required, translatable alternative text (GB13).

## The delivery step

`GET /spark/commerce/shop/delivery/options?country=&postalCode=` lists the methods every seller's parcel of the basket
can be sent with (GC9: a method that does not accept a dangerous-goods class is never offered; GB6: customs and the
DAP/DDP choice per destination zone; GC11: delivery promises that skip holidays). `POST /spark/commerce/shop/delivery`
stores the address, the method and the service point (GB5); the point's address comes from the carrier again, never
from the request. The delivery is a **pricing step** (order 500): one order extra per seller, coded `delivery:{method}`.
"Order with obligation to pay" is refused with `deliveryRequired` while the basket holds goods and no delivery is
chosen, because the total must include delivery (CRD Art. 6(1)(e); *checked*).

When the order is placed the shop records the **least expensive standard delivery** offered at that moment
(`ShopOrder.StandardDeliveryCost`). A withdrawal refunds that much of the delivery and keeps the surcharge for a more
expensive method the buyer chose (CRD Art. 13(2); *checked*): Checkout asks the shop through `IStandardDeliveryCost`.

## Orders: Paid → Shipped → Delivered

Each checkout order with goods gets a `ShopOrder` per seller (`ShopOrders/{orderId}/{seller|platform}`), Paid when the
order is confirmed. In **ShopOnly** mode staff ship through `POST /spark/commerce/shop/staff/ship` (some or all units, one
label per parcel, GB5); Checkout is told per line (I4: a later refund returns no quota until restock). In **Warehouse**
mode the warehouse reports through `ISparkShopOrders.MarkShippedAsync` / `MarkDeliveredAsync` and the shop never books.

**Delivery starts the periods (D44).** Shipping's `Delivered` event (or `MarkDeliveredAsync`) marks a shipment delivered;
a line is reported to Checkout (`ISparkCheckoutRefunds.MarkDeliveredAsync`) once **all** its units are delivered.
Checkout starts the 14-day withdrawal period at the **last** delivery of every goods line of the order (CRD Art.
9(2)(b)(i); a line over several parcels counts from its last parcel, Art. 9(2)(b)(ii); *checked*). The legal
guarantee runs per line from its delivery.

## Returns: three flows (GA1, GA8)

The return reason picks the flow (§4.7): the **unconditional withdrawal** through Checkout's withdrawal function (no
approval, CRD Art. 9–11a; *checked*), a **legal-guarantee claim** (Directive (EU) 2019/771 Art. 13–16; the seller
pays the return shipping), or a **seller-allowed return** within the seller's policy. When Checkout expects goods back
(`ReturnExpected`) the shop records the return and, in ShopOnly mode, books and mails a **return label**
(`SparkShop/ReturnLabel`, GB5).

- The carrier's **first scan** of the return parcel is the buyer's **proof of dispatch**: the shop registers it
  (`RegisterGoodsBackAsync`, `ProofOfDispatch`), so a withheld withdrawal refund is paid "on receipt or proof of dispatch,
  whichever is earlier" (CRD Art. 13(3); *checked*).
- The carrier's **delivery to the seller** registers the **receipt** when `Returns:ReceiptOn` is `CarrierDelivery`
  (disposition `Inspect`); with `StaffInspection`, staff register it.
- **Inspection** (`POST /spark/commerce/shop/staff/inspect`) sets the disposition (`Restock` puts the units back on sale
  exactly once, I4) and may deduct **diminished value** with a reason, while the refund is still withheld (CRD Art.
  14(2); *checked*).

## Reviews (GC5)

Only the buyer of a **delivered** order line can review it, once, within `Reviews:Window`. The storefront states whether
and how reviews are verified (UCPD Directive 2005/29/EC Art. 7(6) and Annex I point 23b as inserted by Directive (EU) 2019/2161;
*checked*): every published review comes from a delivered order, and with `PreModeration` a moderator
(`Review/Moderation`) checks it first. A review is refused only for a listed reason (spam, offensive, unrelated, personal
data, conflict of interest, illegal), **never for its rating** (point 23c, "misrepresenting consumer reviews"; *checked*); a review written for a benefit
says so. Reviews are in the buyer's personal-data export and are deleted with the account. Applications that want
reviews in the generic pages grant `QueryRead/ShopProductReview` to their moderators.

## Product feeds (GC6)

`GET /spark/commerce/shop/feeds/google?seller={organizationId}` (RSS 2.0 with Google's `g:` attributes) and
`/feeds/meta?seller=` (CSV) list every active variant of the seller's published products with its FinalPrice,
availability, GTIN (or `identifier_exists=no`), brand, images, colour and size, the unit-pricing measure (GA3) and the
product group. Omit `seller` (or pass `platform`) for the platform's own products. The URLs are stable and cached for
`Feeds:CacheDuration`. They follow the platforms' public specifications (Google Merchant Center product data
specification; Meta catalogue fields); neither platform is called. CSV cells that start with `= + - @` are prefixed
with `'` against formula injection.

## Public pages

Published products get server-rendered meta tags (X6), a schema.org `Product` with one `Offer` per variant at its
FinalPrice and availability (X22), and an entry in `/sitemap.xml` (`/sitemaps/products/{page}.xml`). DSA notices
(GA5) accept the kind `product`.

## Angular

`@mintplayer/ng-spark/commerce/shop` exports `SparkCommerceShopService` and three components:
`<spark-shop-catalog>` (search, facets, price ranges, sort, the ranking note), `<spark-shop-product [slug]>` (gallery per
variant, FinalPrice with the unit price, availability, add to basket, the safety block, the guarantee notice, the
withdrawal exemption, reviews with the verification statement) and `<spark-shop-delivery-step>` (address, rates,
service-point picker, the choice). M17 wires them into routed, replaceable pages (`withShopRoutes()`).
