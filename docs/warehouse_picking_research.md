# Warehouse order picking: research

Date: 2026-10-17. Evidence for the warehouse order-picking PRD, `docs/warehouse_picking_PRD.md`.

The commerce suite (#521) decided that a reusable, generic warehouse order-picking plugin ships in the same pull request as
commerce, with a PRD of its own (commerce D13), and that the production shop and the production warehouse are two separate
web applications that consult each other's information (commerce D12). This file collects the evidence that PRD cites. It
summarises what a warehouse management system (WMS) for order picking covers and how established systems model it; what
browsers and warehouse hardware allow for scanning, label printing and offline work; how Spark's cross-module replication
fits the production Community licence, and the defects found in the replication code that ships today; which cross-app
transports remain; the semantics of the shop↔warehouse boundary, including a stock-feed formula and why it does not
oversell; the proposed v1 scope and the framework additions it needs; and the spikes that would settle the remaining
risks.

**The PRD's decision log is authoritative for decisions; this file holds evidence only.** Where a passage below
recommends something, it records the researcher's reading of the evidence on this date. The grilling departed from it in
places (for example on the transport, zone picking and device sign-in), so check the PRD before acting on a
recommendation here.

**Method and limits.** Two read-only sweeps on 2026-10-17: a domain sweep on the web and a Spark-fit sweep of the
repository (no builds, no tests). The web-search budget ran out early, so most external sources are pages fetched
directly: official documentation, standards datasets and source files on GitHub. A sub-agent read the primary documents
behind §1: the de Koster, Le-Duc & Roodbergen 2007 review, Bartholdi & Hackman's *Warehouse & Distribution Science*
v0.98.1, the Microsoft Dynamics 365 Supply Chain Management (SCM) and Business Central documentation, Odoo 18, and GS1 and
WERC pages. Claims that came from background knowledge and were not re-checked are marked [unverified].

**Conventions**
- Repository paths are relative to the repository root, read on 2026-10-17 at master `e39b16de`. Abbreviations:
  `R/` = `libs/replication/MintPlayer.Spark.Replication/` · `RA/` = `libs/replication/MintPlayer.Spark.Replication.Abstractions/` ·
  `S/` = `libs/spark/MintPlayer.Spark/` · `A/` = `libs/spark/MintPlayer.Spark.Abstractions/` ·
  `Msg/` = `libs/messaging/MintPlayer.Spark.Messaging/` · `MsgA/` = `libs/messaging/MintPlayer.Spark.Messaging.Abstractions/` ·
  `SW/` = `libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/` ·
  `Auth/` = `libs/authorization/MintPlayer.Spark.Authorization/` · `NS/` = `libs/node_packages/ng-spark/` ·
  `E2E/` = `tests/MintPlayer.Spark.E2E.Tests/`.
- **Every repository file:line below was re-read against the checkout while this file was compiled** (a separate
  verification pass over 76 references, the rest directly), and the corrections from that pass are applied. External
  pages were not re-fetched.
- `docs/commerce_PRD.md` was being edited on this date. Its line numbers below were re-read late on 2026-10-17 and will
  drift; its section and item ids (I1, X12, F12, S20) are the stable references.
- **[unverified]** marks a claim the research flagged as not confirmed by a fetched page, or as needing a check before
  it is relied on.
- **(inference)** marks reasoning, not a documented or measured fact.
- "B&H" = Bartholdi & Hackman, *Warehouse & Distribution Science* v0.98.1; "de Koster" = de Koster, Le-Duc & Roodbergen
  2007 (EJOR). Section and page numbers after them are the source's own.
- Vendor pages are used for mechanisms more than for effectiveness numbers. The textbook cost shares are old estimates,
  repeated ever since.

**Key findings**
1. **The recurring WMS core is small.** Every system studied has an append-only movement ledger, a per-location balance,
   an "allocated" quantity next to "on hand", pluggable removal rules (FIFO, FEFO, closest location), grouped pick work
   (batch, wave, cluster) and a fulfilment handshake (accept/reject, cancel accept/reject) (§1).
2. **Picking is the dominant cost and travel dominates picking.** WMSs sequence locations along a path instead of
   modelling geometry, so routing by a per-location walk sequence gets most of the benefit (§1.3, §1.4).
3. **Browser scanning is workable but uneven.** `BarcodeDetector` exists only on Chrome Android, macOS and ChromeOS, so a
   self-hosted WASM ponyfill is mandatory. A Zebra keyboard wedge reaches a web page; DataWedge intents do not. A
   US-layout USB scanner on an AZERTY host mangles digits (§2).
4. **A browser cannot print raw to port 9100.** Silent label printing needs a bridge (QZ Tray, Zebra Browser Print), an
   on-site agent or a server on the LAN. The PDF print dialog always works (§2.6).
5. **RavenDB ETL, and with it every `[Replicated]` replica, does not exist on the production Community licence.** Local
   development and CI run the Developer licence, so an ETL design passes every local and CI test and fails in production
   (§3.2).
6. **The replication code that ships today has ten defects, WF1–WF10**, two of them security issues; one more gap was
   noted while compiling this file (§4).
7. **On Community, a producer-declared push over module-authenticated HTTPS**, with a versioned idempotent apply and a
   digest reconciliation, needs no ETL, no new subscription and no cross-database credentials (§5).
8. **The stock feed must not be a single "available" number.** Cumulative counters in one document keep the shop's
   no-oversell invariant exact for a single-channel SKU despite late, reordered or duplicated updates, and they expose a
   conflict with commerce I4 for shipped units (§6.4).
9. **Missing building blocks, mostly shared with commerce:** barcode scanning, 1D barcode and PDF rendering, offline
   data, units of measure and a hierarchical reference picker (§3.8, §7.5).

---

## 1. The WMS domain

### 1.1 What a WMS for order picking covers

**Core definition.** A WMS is a SKU database plus a **stock locator system**: it manages "both the inventory of skus and
the inventory of storage locations". Directed put-away and directed picking depend on that
([B&H 2019, ch. 4](https://www.warehouse-science.com/book/editions/wh-sci-0.98.1.pdf)).

**Master data**
- **Hierarchy** (D365 as the reference): warehouse → zone groups and zones → locations. A location profile sets the
  location type and naming format; whether inventory statuses may mix; the batch-mixing rule by expiry; "Allow cycle
  counting"; dimensions; stocking limits; fixed picking locations per item; and blocking causes
  ([configure locations](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/tasks/configure-locations-wms-enabled-warehouse)).
- **Naming.** D365 location formats are multi-segment ("aisle, rack, shelf, and bin"; at most 10 characters in the
  example). B&H §10.2.2: when close locations have similar addresses, the pick list itself tells the picker where to go
  next.
- **Check digits.** D365 prompts for the location check digit; the step is skipped if the location has none or was keyed
  manually ([location confirmation](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/batch-and-license-plate-confirmation)).
  No standard for location-label layout was found.
- **Location types.** Business Central has RECEIVE, SHIP, PUT AWAY (bulk), PICK, PUTPICK and QC, and "You can choose to
  operate with just the RECEIVE, PUTPICK, SHIP, and QC bin types"
  ([BC warehouse design](https://learn.microsoft.com/en-us/dynamics365/business-central/design-details-warehouse-management)).
  D365 needs at least staging and final-shipping types, plus SORT for put walls. Odoo's multi-step routes create Input,
  Quality Control, Packing Zone and Output locations.
- **Units of measure.** D365 "unit sequence groups" run smallest to largest (Each-Box-PL) and drive work units, license
  plate grouping and counting; a count of "2 boxes + 5 pieces" is summed through conversion
  ([UoM stocking policies](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/unit-measure-stocking-policies)).
  A line for 15 eaches can become 1 carton of 12 plus 3 eaches (B&H §3.3).
- **Lots, serials, expiry.** BC's "Pick According to FEFO" breaks ties by lowest lot or serial, then by earliest
  registration (BC page above).

**Inbound**
- **Receiving** can start from an advance shipping notice and takes about 10% of operating cost (Frazelle, via B&H §3.1).
  Scanning the SSCC matches physical units to the EDI messages about them, which enables automated receiving and
  cross-docking ([GS1 Logistic Label Guideline 1.3](https://www.gs1.org/standards/gs1-logistic-label-guideline/current-standard)).
  Odoo's three-step receipt keeps goods unavailable until they leave Quality Control
  ([receipts in three steps](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/receipts_three_steps.html)).
- **Put-away** takes about 15% of operating expense. It manages "a second inventory, not of product, but of storage
  locations", and the location should be scanned (B&H §3.2).
- **Storage policies** ([de Koster §4](https://pure.eur.nl/ws/portalfiles/portal/46713708/DesignandControl_2007.pdf)):
  random; closest open location; dedicated (lowest space use, but pickers learn the locations; common for pick areas,
  with random storage in bulk); full-turnover; class-based (about 15% of SKUs carry about 85% of turnover; 2–4 classes
  are typical). Direct put-away goes to reserve or to primary (pick) locations, with replenishment between them
  ([working-paper version](https://repub.eur.nl/pub/7322)).

**Stock**
- **Cycle counting in D365** ([cycle counting](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/cycle-counting)):
  triggered by thresholds or plans; always covers a whole location; spot counts supported; **blind**: "The system never
  shows the expected quantity to count. This design prevents intentional miscounts"; out-of-tolerance counts go to
  "Pending review". Odoo uses a per-location frequency; BC uses item counting periods.
- **ABC count frequencies.** The only concrete figures found are a Dynamics GP example: **A every 30 days, B every 60,
  C every 120** ("or you might not count them at all"), spread evenly over the cycle
  ([GP docs](https://github.com/MicrosoftDocs/msftdynamicsgpdocs/blob/85592665fc2e196fa78827ae8df215e306e29cc0/dynamics-gp/distribution/Inventory2.md)).
  There is no normative standard.
- **Adjustments** carry reason codes for analysis and accounting
  ([D365 reason codes](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/reason-codes-for-counting-journals)).
  BC posts warehouse adjustments against a virtual adjustment bin, then syncs them to the item ledger
  ([BC count/adjust](https://learn.microsoft.com/en-us/dynamics365/business-central/inventory-how-count-adjust-reclassify)).
- **Replenishment.** D365 offers min/max, wave demand, load demand and immediate. Min/max "typically… runs once every
  day" before picking, and "can't replenish an empty location unless you set the location as the fixed location for the
  item" ([replenishment](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/replenishment)). A
  restock costs more than a pick; the rule of thumb is about one restocker per five pickers (B&H §3.3).

**Outbound: allocation and pick methods**
- **Allocation.** Odoo's removal strategies are FIFO, LIFO, FEFO (by removal date), Closest Location (by location
  sequence or name) and Least Packages; the product category's "Force Removal Strategy" overrides the location
  ([removal strategies](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/removal_strategies.html)).
- **Taxonomy** (de Koster pp. 483–484): picker-to-parts, parts-to-picker or put systems; pick-by-order (discrete) versus
  pick-by-article (batch); sort-while-pick versus pick-and-sort; zoning, progressive (pick-and-pass) or synchronised; wave
  picking releases orders for a common carrier departure, sized by pick time, "often between 30 minutes to 2 hours". More
  than 80% of Western European systems are low-level, manual picker-to-parts: exactly the case a browser-based WMS
  serves.

| Method | Fits when | Source |
|---|---|---|
| Discrete | Large orders "will have sufficient pick density on their own" | B&H §3.3 |
| Batch | Single-line orders: "almost always better to batch… no sortation is required". Medium orders: batch when separation and space cost less than the extra walking | B&H; [Odoo batch](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/batch.html) |
| Sort-while-pick vs pick-then-sort | Sorting while picking slows pickers and invites errors; downstream sorting costs space and labour | B&H |
| Zone | Many small orders; an assembly line that needs balancing; orders need consolidation | B&H; de Koster §5 |
| Wave | Carrier cut-offs; high volume | de Koster; Odoo wave (§1.5) |
| Cluster (pick-to-cart) | "High order volumes where organization and speed are critical" | Odoo cluster (§1.5) |

- **Technologies.** Pick-to-light flow rack reaches 150–500 picks per person-hour (B&H §5.1.3). Put systems average
  about 500 puts per hour, up to 1,000 (de Koster working paper). D365 put walls sort by order, shipment or load
  ([put-to-wall](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/put-to-wall-put-to-store)).

**Pick execution and exceptions**
- The pick list can be paper, labels, lights, RF or voice. The WMS sequences lines in travel order, and product should be
  scanned "at all key decision points" (B&H §3).
- **D365 piece-picking confirmation** requires a scan per piece up to a configured maximum, and honours quantity and UoM
  encoded in the barcode ([piece picking](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/piece-picking-confirmation)).
- **D365 short pick** ([short-pick reallocation](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/tasks/set-up-short-picking-item-reallocation)):
  "Adjust inventory" sets the location to 0 and can remove the line's reservations; reallocation is automatic (location
  directives), manual (the worker picks from unreserved stock) or both. An automatic count after a short pick is **not**
  documented, so a "trigger a count" option would be our own choice.
- **Totes.** Odoo records the per-order tote as each move's Destination Package; totes carry license-plate identities
  (B&H ch. 11). No source was found for substitutions.

**Packing, cartonisation, labels, handover**
- Packing is where accuracy gets checked: little walking, lots of handling. Customers want as few containers as possible,
  so partial orders are staged or shipped as partials (B&H §3.4). D365 packing stations verify items, choose the
  container, close it and move it to the outbound dock
  ([packing containers](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/packing-containers));
  containerization uses container types and groups
  ([wave containerization](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/wave-containerization)).
- **Cartonisation libraries:** [3DContainerPacking](https://github.com/davidmchapman/3DContainerPacking) (C#, MIT,
  EB-AFIT, last push 2022); [SixFourThree.BoxPacker](https://github.com/StephenPAdams/SixFourThree.BoxPacker), a C# port
  of [dvdoug/BoxPacker](https://github.com/dvdoug/BoxPacker) (PHP, MIT, "e-commerce cartonization", weight as a
  constraint; last push 2023); [skjolber/3d-bin-container-packing](https://github.com/skjolber/3d-bin-container-packing)
  (Java, active). No comparison of simple volume rules against 3D heuristics was found.
- **Dimensional weight** = L×W×H ÷ divisor (for example 5,000 cm³/kg for DHL Express, 139 in³/lb for FedEx); billable
  weight is the greater of actual and dimensional ([Wikipedia, secondary](https://en.wikipedia.org/wiki/Dimensional_weight);
  check carrier tariffs).
- **SSCC** is "the only mandatory requirement" of the GS1 Logistic Label, as GS1-128 in the lowest barcode (GS1
  guideline above).
- **Manifesting.** Carton and trailer manifesting are "high-end" features (B&H §4.3). Odoo Dispatch Management covers load
  building per carrier and dock ([dispatch](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/setup_configuration/dispatch.html)).
  No source was found on parcel end-of-day close.

**Returns**
- D365 RMA steps: return order → arrival and inspection → validation → optional quarantine → disposition → packing slip
  ([sales returns](https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/sales-returns)).
- D365 disposition actions: Credit only, Credit (return to inventory), Replace and credit, Replace and scrap, Return to
  customer; return reason codes are separate
  ([disposition codes](https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/disposition-and-return-reason-codes)).
- Scale (dated): returns run about 5% in retail and 25–30% in e-commerce, and handling a return can cost "up to ten
  times" the outbound shipping (B&H §3.4, §3.7).

**Labour and productivity metrics**
- **WERC DC Measures 2026:** 36 metrics on a quintile scale. The five most used are on-time shipments, dock-to-stock
  time, average and peak capacity used, and backorders as a percentage of lines
  ([DC Measures](https://werc.org/page/ASSESS-DC_Measures)). The public metric list
  ([benchmarking](https://werc.org/page/ASSESS-Benchmarking_Overview)) adds order cycle time, lines received and put away
  per hour, line and order fill rate, lines and orders picked and shipped per hour, inventory count accuracy by location,
  order picking accuracy and perfect-order components. **Benchmark values are paywalled** [unverified].
- Public definitions in the D365 warehouse performance content: put-away time, dock-to-stock, on-time receipts, shipped
  in full and on time, location inventory accuracy
  ([Power BI content](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/analytics/warehouse-power-bi-content)).
- Picks per person-hour is the standard productivity measure; work per order = lines per order × person-hours per pick
  (B&H §3.3). The events these need are cheap to record from day one.

### 1.2 Core versus extended

- **Gartner:** core WMS = "receive, put away, store, count and pick, pack and ship goods" via mobile devices and
  barcodes; "extended" = labour management and optimising inventory location
  ([Gartner WMS market](https://www.gartner.com/reviews/market/warehouse-management-systems)).
- **B&H tiers (§4.3).** Basic: receiving, QA, put-away, location tracking, picking, packing, shipping. High-end:
  RF-directed operation, cycle counting, manifesting, replenishment, value-added services, returns, pick/put-to-light,
  yard, waves, labour, task interleaving. Advanced: multi-DC view, slotting, EDI, parcel shipping.
- **BC tiers:** basic order-by-order; advanced consolidated worksheets; "Directed put-away and pick" (bins, ranking,
  capacity, zones, classes, multiple UoMs per bin).
- **Enterprise extras (D365):** demand-based
  [slotting](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/warehouse-slotting), containerization,
  put walls, [MHAX](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/mhax) for automation, planned
  cross-docking. Yard management is usually a separate YMS (B&H ch. 4).
- **3PL billing:** no primary source found. Odoo's consignment covers stock owned by someone else, but has no billing
  engine ([owned stock](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/owned_stock.html)).
- **Read-across to §7 (research reading):** v1 = Gartner's "core", plus from B&H's high-end tier RF-directed operation,
  cycle counting, replenishment, returns and simple waves. Everything Gartner calls "extended" and B&H calls "advanced"
  stays out.

### 1.3 The cost of picking

- Order picking is up to **55%** of warehouse operating expense (Tompkins et al. 2003 via de Koster; Frazelle 1996 via
  B&H). Roodbergen says "as much as 60% of all labor activities"
  ([roodbergen.com](https://www.roodbergen.com/warehouse/background.php)).
- Picker time is roughly half travel: travel 50%, search 20%, pick 15%, setup 10% (de Koster).
- These are old textbook estimates, repeated ever since. They still say where the money is: in travel, which routing
  and batching attack.

### 1.4 Routing by walk sequence, and batching

**Routing heuristics** (de Koster §7):
- **S-shape / traversal:** fully traverse every aisle that has a pick.
- **Return:** enter and leave each aisle from the same end.
- **Midpoint:** front-half picks from the front, back-half picks from the back.
- **Largest gap:** skip the largest gap within each aisle.
- **Combined / composite:** traverse or return per aisle, chosen by dynamic programming.

**Optimal routing.** Ratliff & Rosenthal 1983 solve the single-block case in time linear in the number of aisles
([IDEAS](https://ideas.repec.org/a/inm/oropre/v31y1983i3p507-521.html)). Pansart et al. generalise it to any number of
cross-aisles ([arXiv](https://arxiv.org/pdf/1703.00699)).

| Finding | Source |
|---|---|
| The best heuristic averages ~5% above optimal (random storage) | Petersen 1997 ([DOI](https://www.doi.org/10.1108/01443579710177860)) via de Koster |
| S-shape routes are 7–33% longer than optimal (single block) | De Koster & Van der Poort 1998 via de Koster |
| Largest gap beats S-shape below ~4 picks per aisle; midpoint beats S-shape at ~1 pick per aisle | Hall 1993 via de Koster |
| Multi-block: combined+ is best in 74 of 80 instances | Roodbergen & de Koster 2001 via de Koster |

**Why practice uses heuristics, and why that favours a walk sequence.** Optimal routes "seem illogical", so pickers
deviate; optimal algorithms ignore congestion; and B&H know of "no warehouse management systems… [that] manage an
explicit geometric model of the layout". WMSs instead sequence locations along a path outline (serpentine,
branch-and-pick) (B&H ch. 10). This is the strongest argument for v1 routing by a per-location `WalkSequence` (research
reading). Largest-gap or combined routing needs aisle and position geometry; storing those fields in v1 keeps the
heuristics possible later.

**Batching** (de Koster §6):
- Proximity batching and time-window batching; the problem is NP-hard.
- **Seed** heuristics start from a seed order, then add the orders needing the fewest extra aisles. **Savings**
  heuristics follow Clarke & Wright.
- Simple batching significantly beats first-come-first-served. Seed algorithms do best with S-shape routing and large
  carts; savings algorithms do best with largest gap and small carts.
- Typical percentage savings could not be verified.

**Interactive check.** [Roodbergen's Interactive Warehouse](https://www.roodbergen.com/warehouse/index.php) compares
routing methods on a drawn layout; useful as a sanity check for spike W8's route quality.

### 1.5 How existing systems model it

**Microsoft Dynamics 365 SCM and Business Central.** The patterns worth taking are in §1.1 and §1.2: location profiles,
location check digits, unit sequence groups, blind cycle counts with a review state, min/max replenishment limited to
fixed locations, piece-picking confirmation, short-pick reallocation, put walls, packing stations, RMA dispositions and,
in BC, the minimal RECEIVE / PUTPICK / SHIP / QC bin set and FEFO tie-breaks. Microsoft's Warehouse Management mobile app
runs on Windows, Android and iOS
([install page](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/install-configure-warehouse-management-app));
no offline statement was found.

**Odoo Inventory (17/18/19)**
- **Locations, routes, operation types.** Multi-step delivery creates separate operation types: three-step delivery
  yields Pick, Pack and Delivery, with WH/Stock → Packing Zone → Output → customer
  ([Odoo 13 three-step](https://www.odoo.com/documentation/user/13.0/uk/inventory/management/delivery/three_steps.html);
  current page list in the [Odoo 18 inventory index](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory.html)).
  The operation type carries the behaviour
  ([operation types, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/operation_type.html)):
  "Reservation Method" (*At Confirmation*, *Manually*, *Before scheduled date*); "Create Backorder" (*Ask*, *Always*,
  *Never*); default source and destination locations; a barcode and Barcode-app scan requirements.
- **Removal strategies:** FIFO, LIFO, FEFO, Closest Location and Least Packages. **FEFO is driven by the removal date,
  not the expiry date**: the removal date is the expiration date minus the product's "Removal Date" days. It requires lot
  or serial tracking with expiration dates and is set through the category's "Force Removal Strategy"
  ([FEFO, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/removal_strategies/fefo.html)).
- **Putaway rules.** A rule has "When product arrives in", Product or Product Category (optionally a package type) and
  "Store to". "Sublocation" offers No, Last Used or Closest Location (which needs a storage category). The first match
  wins in this priority: package type + product; package type + category; package type; product; category. Storage
  categories cap a location's capacity
  ([putaway, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/putaway.html)).
- **Batch, wave and cluster** share one setting, "Batch, Wave & Cluster Transfers".
  - A **wave** is "a batch transfer with an extra step: transfers are split first, then grouped". It holds only lines of
    the same operation type. "Prepare Wave" groups lines by source location; automatic waves group by product or product
    category. A wave has a Responsible, an optional Docks Location and a Vehicle. The page says nothing about pick-route
    sequencing ([wave, 19.0](https://www.odoo.com/documentation/19.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/wave.html)).
  - **Cluster** picking uses reusable packages ("Package Use = Reusable Box") as per-order cart slots. Each line's
    "Destination Package" is the order's package, so no separate sort step is needed. Odoo lists its own limits: no
    urgent-order prioritisation, batches built manually beforehand, possible bottlenecks
    ([cluster, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/cluster.html)).
- **Cycle counts** are per location ("Inventory Frequency" in days, under "Cyclic Counting"), plus an annual inventory day
  (default 31 December). The page does not mention ABC
  ([cycle counts, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/cycle_counts.html)).
- **Reordering rules:** Min, Max and Multiple Quantity on *forecasted* stock; the trigger is Auto or Manual, and Manual
  lists the product on the replenishment dashboard
  ([reordering rules, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/replenishment/reordering_rules.html)).
- **GS1 in the Barcode app.** The default GS1 nomenclature covers AIs 00, 01, 02, 10, 21, 13, 15, 17, 30, 37,
  310x–365x, 410, 413, 414 and 91. A variable-length field that is not last must end with FNC1 (`\x1D`). "Odoo requires
  all rules to be listed in the barcode nomenclature for Odoo to read the barcode": the AI dictionary is data, a concept
  worth copying ([GS1 nomenclature, 18.0](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/barcode/operations/gs1_nomenclature.html)).
  The `stock_barcode` addon is not in the Community repository
  ([odoo/odoo 18.0 path returns 404](https://github.com/odoo/odoo/tree/18.0/addons/stock_barcode)), consistent with
  Barcode being an Enterprise app.
- **Licence:** Odoo Community is LGPLv3 and Enterprise is proprietary [unverified]. Learn from the concepts only.

**ERPNext / Frappe (v15)**
- **Stock granularity is the Warehouse tree.** `Bin` is the per item × warehouse balance, not a physical bin. Its fields
  ([bin.json](https://raw.githubusercontent.com/frappe/erpnext/develop/erpnext/stock/doctype/bin/bin.json)):
  `actual_qty`, `reserved_qty`, `ordered_qty`, `indented_qty` (Requested), `planned_qty`, `projected_qty`,
  `reserved_qty_for_production`, `reserved_qty_for_sub_contract`, `reserved_qty_for_production_plan`, `reserved_stock`.
- **"Projected Qty"** = Actual + Planned + Requested + Ordered − Reserved − Reserved for Production − Reserved for
  Subcontracting − Reserved for Production Plan ([projected quantity](https://docs.frappe.io/erpnext/projected-quantity)).
- **Pick List** purposes: Delivery, Material Transfer for Manufacture, Material Transfer. It picks warehouses **FIFO** and
  **the batch nearest expiry** for batched items; it has "Get Item Locations", Scan Mode with "Prompt Qty", and "Pick
  Manually"; it produces a Delivery Note or Stock Entry ([pick list](https://docs.frappe.io/erpnext/pick-list)).
- **Stock Reservation Entry** (new in v15) reserves against a Sales Order per warehouse. It can be created from a Pick
  List and released by Unreserve or by cancelling it ([stock reservation](https://docs.frappe.io/erpnext/stock-reservation)).
- **Putaway Rule** is per Item × Warehouse, with Capacity and Priority (1 = highest). It fills higher-priority warehouses
  first; ties go to the one with more free space; overflow spills to the next rule
  ([putaway rule](https://docs.frappe.io/erpnext/putaway-rule)).
- **Idiosyncrasy to avoid:** modelling bins as warehouses. It works, but it mixes site and slot semantics in one tree.

**Apache OFBiz**
- `FacilityLocation` is keyed by (facilityId, locationSeqId) with `areaId`, `aisleId`, `sectionId`, `levelId`,
  `positionId` and `locationTypeEnumId`. `ProductFacility` holds `minimumStock`, `reorderQuantity` and `daysToShip`;
  `ProductFacilityLocation` holds `minimumStock` and `moveQuantity`, which is pick-face replenishment per location
  ([product-entitymodel.xml](https://raw.githubusercontent.com/apache/ofbiz-framework/trunk/applications/datamodel/entitydef/product-entitymodel.xml)).
- **`InventoryItem`** holds both **`quantityOnHandTotal`** and **`availableToPromiseTotal`**, plus `lotId`, `expireDate`,
  `locationSeqId`, `serialNumber` and `statusId`. Every change is an **`InventoryItemDetail`** delta row
  (`quantityOnHandDiff`, `availableToPromiseDiff`, `reasonEnumId`, `orderId`, `shipmentId`, `physicalInventoryId`): the
  ledger-plus-projection pattern in its purest form (same file).
- Picking: `Picklist` (facility, shipment method, status) → `PicklistBin` (`binLocationNumber`, `primaryOrderId`) →
  `PicklistItem` (quantity, inventoryItemId). `PicklistBin` is in effect the cluster-picking "tote per order". Packing:
  `ShipmentPackage` (weight, box type) → `ShipmentPackageContent`; `ShipmentBoxType` and `CarrierShipmentBoxType` map box
  types to carrier codes. Shipping: `ShipmentRouteSegment` is one carrier leg; `ShipmentPackageRouteSeg` holds tracking
  and label data per package
  ([shipment-entitymodel.xml](https://raw.githubusercontent.com/apache/ofbiz-framework/trunk/applications/datamodel/entitydef/shipment-entitymodel.xml)).

**OpenBoxes**
- An open-source WMS and inventory system for healthcare and disaster-response supply chains, EPL-1.0, built on Grails
  ([docs](https://docs.openboxes.com/en/latest/), [GitHub](https://github.com/openboxes/openboxes)). It tracks
  inventory "by lot and expiry" ([features](https://docs.openboxes.com/en/latest/about/features/)).
- Hierarchy: depot → zone (for example a cold room) → bin. **Locations already used cannot be deleted, only
  deactivated** ([bins and zones](https://help.openboxes.com/article/289-managing-bin-and-zone-locations)): a rule worth
  copying, since a location with ledger history must never disappear.
- The public docs are thin on picking and putaway, so it is of low value as a modelling reference.

**Dolibarr**
- Physical stock versus **virtual stock**, "the stock that will remain after all open/pending actions (that affect
  stocks) have been performed". The decrement trigger is configurable: `DeStockOnValidateOrder`, `DeStockOnShipment`,
  `DeStockOnShipmentOnClosing` or `DeStockOnBill`. "Desired stock" drives replenishment; lots and serials carry eat-by and
  sell-by dates ([stocks.lang](https://raw.githubusercontent.com/Dolibarr/dolibarr/develop/htdocs/langs/en_US/stocks.lang)).
- Lesson (research reading): a configurable decrement trigger is a sign of a missing allocation concept. Decrement on
  ship-confirm and model everything before that as allocation.

**Shopify: the closest public model of our boundary**
- Inventory states: `available`, `committed`, `incoming`, `on_hand`, `reserved`, `damaged`, `safety_stock`,
  `quality_control`. `on_hand` = available + committed + reserved + damaged + safety_stock + quality_control; `incoming`
  is not included. Shopify itself manages `committed` (orders, draft orders, transfers ready to ship); apps cannot adjust
  it. Mutations: `inventorySetQuantities`, `inventoryAdjustQuantities` (delta), `inventoryMoveQuantities`. Concurrency:
  compare-and-swap through `changeFromQuantity` (fails with `CHANGE_FROM_QUANTITY_STALE`), an `@idempotent(key:)`
  directive, and `referenceDocumentUri` for traceability
  ([inventory states](https://shopify.dev/docs/apps/build/orders-fulfillment/inventory-management-apps/manage-quantities-states)).
- **Fulfillment service apps.** Each service gets its own location. Shopify posts `FULFILLMENT_REQUEST` and
  `CANCELLATION_REQUEST` notifications to `<callback_url>/fulfillment_order_notification`; the app accepts or rejects with
  `fulfillmentOrderAccept…/Reject…FulfillmentRequest` and `…CancellationRequest`. Partial fulfilment lists
  `fulfillmentOrderLineItems` with quantities; `fulfillmentCreate` carries `trackingInfo`. An optional
  `/fetch_stock.json` returns SKU → `on_hand`; it is called on setup and on SKU changes, with a full sync hourly. An
  example rejection message: "This was already picked up by the courier"
  ([build for fulfillment services](https://shopify.dev/docs/apps/build/orders-fulfillment/fulfillment-service-apps/build-for-fulfillment-services),
  [overview and webhook topics](https://shopify.dev/docs/apps/build/orders-fulfillment/fulfillment-service-apps)).
- `FulfillmentOrderStatus`: OPEN, IN_PROGRESS, ON_HOLD, SCHEDULED, INCOMPLETE, CANCELLED, CLOSED
  ([enum](https://shopify.dev/docs/api/admin-graphql/latest/enums/FulfillmentOrderStatus)).
  `FulfillmentOrderRequestStatus`: UNSUBMITTED, SUBMITTED, ACCEPTED, REJECTED, CANCELLATION_REQUESTED,
  CANCELLATION_ACCEPTED, CANCELLATION_REJECTED, CLOSED
  ([enum](https://shopify.dev/docs/api/admin-graphql/latest/enums/FulfillmentOrderRequestStatus)).

**ShipBob** ([orders integration](https://developer.shipbob.com/2026-07/guides/build-orders-integration.md),
[webhooks](https://developer.shipbob.com/2026-07/webhooks.md), [inventory](https://developer.shipbob.com/2026-07/guides/inventory.md))
- `reference_id` is the **idempotency key**: a duplicate (`reference_id`, channel) returns 422.
- Orders and shipments can be cancelled only if they "haven't been picked, packed or shipped yet". One order can produce
  several shipments.
- Webhooks are at-least-once and **may arrive out of order** ("sort by payload timestamps"). Retries back off out to
  10-hour intervals; an endpoint failing for 5 days is disabled.
- Inventory is tracked at component level, not bundle SKU. To avoid overselling, set a **buffer** in the integration
  layer (send 0 below a threshold).

**ShipHero** ([webhooks](https://developer.shiphero.com/webhooks/)): topics include Inventory Update/Change, Order
Allocated/Deallocated, Order Backorder, Order Canceled, Shipment Update, Order Packed Out, Tote Complete and Generate Label.
Respond within 10 s; ShipHero retries 5 times. Delivery is not guaranteed, so run **reconciliation jobs** with
`updated_at_min`. Dedupe on `X-Shiphero-Message-ID`.

**ShipStation:** label formats `pdf`, `png` and `zpl`, 4×6 by default; ZPL cannot print on letter sheets
([label formats](https://docs.shipstation.com/apis/shipengine/docs/labels/formats.md)). Locations are named "area - unit -
shelf - bin" under a warehouse ([inventory](https://docs.shipstation.com/managing-inventory.md)).

**Sortly** (small-warehouse SaaS): items, custom folders, barcode/QR label generation, pick lists, low-stock alerts,
"Offline Mobile Access", webhooks and an API ([features](https://www.sortly.com/features/)). Zoho Inventory (picklists,
packages, bins) could not be fetched (403/404) [unverified].

**Open-source commerce stock models (the shop side of the boundary)**
- **Magento 2 MSI.** It cannot decrement stock at checkout, because stock quantity is an index value. It appends
  **reservations** instead: rows of `reservation_id`, `stock_id`, `sku`, `quantity` and `metadata` (for example
  `"event_type":"order_placed"`). Salable qty = indexed qty + Σ reservations − out-of-stock threshold. Order placed appends
  −n; shipment, cancel and credit memo append compensations. Reservations are append-only ("doesn't remove or modify
  already created reservations"), so "no checks and blocking operations (locks) needed"; zero-sum pairs are cleaned
  periodically ([MSI wiki](https://github.com/magento/inventory/wiki/Salable-Quantity-Calculation-and-Mechanism-of-Reservations)).
- **Vendure.** `saleable = stockOnHand − allocated − outOfStockThreshold` (a negative threshold allows backorders).
  Allocation happens at PaymentAuthorized/Settled; at fulfilment the allocation becomes a Sale and stockOnHand decreases.
  Movement types: Allocation, Sale, Cancellation, Release, StockAdjustment. Pluggable `StockLocationStrategy`,
  `StockAllocationStrategy` and `StockDisplayStrategy` ([stock control](https://docs.vendure.io/guides/core-concepts/stock-control/)).
- **Saleor.** `Stock` has `quantity`, `quantityAllocated` and `quantityReserved`
  ([stock](https://docs.saleor.io/developer/stock/overview)). Allocation at checkout completion, with strategy
  `PRIORITIZE_SORTING_ORDER` or `PRIORITIZE_HIGH_STOCK`, and `InsufficientStock` errors
  ([allocation](https://docs.saleor.io/developer/stock/stock-allocation)). Checkout reservations with
  `reserveStockDuration*` and `stockReservationExpires` ([reservation](https://docs.saleor.io/developer/stock/stock-reservation)).
- **Medusa v2.** `InventoryLevel` per location has `stocked_quantity`, `reserved_quantity` and `incoming_quantity`. A
  `ReservationItem` is created per inventory item when an order is placed; the docs even give a ticket-hold example
  ([inventory concepts](https://docs.medusajs.com/resources/commerce-modules/inventory/concepts)).

### 1.6 Recurring concepts versus idiosyncrasies

| Concept | Where it recurs | Take it? (research reading) |
|---|---|---|
| Append-only movement ledger + balance projection | OFBiz `InventoryItemDetail`, ERPNext Stock Ledger, MSI reservations, Vendure `StockMovement` | **Yes**: the audit trail and the idempotency anchor |
| Separate "allocated/reserved" next to on-hand | OFBiz ATP vs QOH, ERPNext `reserved_qty`, Saleor `quantityAllocated`, Vendure `stockAllocated`, Shopify `committed` | **Yes** |
| Allocation rows linking order line ↔ stock | OFBiz `OrderItemShipGrpInvRes`, Saleor `Allocation`, ERPNext Stock Reservation Entry | **Yes**, on the outbound line |
| Removal/allocation rule as a pluggable strategy | Odoo (5 rules), ERPNext FIFO / FEFO batch, Vendure strategies | **Yes** (FIFO, FEFO, closest; custom through an interface) |
| Grouped pick work: batch / wave / cluster | Odoo batch/wave/cluster, OFBiz `Picklist`/`PicklistBin`, ShipHero totes | **Yes** (discrete, batch, cluster; wave as a release rule) |
| Step pipeline (pick → pack → ship as separate transfers) | Odoo routes | **Partly**: a fixed v1 pipeline, configurable only by skipping steps |
| Packages / handling units, box types | Odoo packages, OFBiz `ShipmentPackage` / `ShipmentBoxType` | **Yes** (cartons + optional SSCC) |
| Backorder on partial validation | Odoo "Create Backorder", Shopify partial fulfilments, ShipBob split shipments | **Yes**, with the policy coming from the shop |
| Accept/reject + cancel-request handshake | Shopify `FulfillmentOrderRequestStatus`, ShipBob cancel-before-pick | **Yes** |
| Configurable "when to decrement stock" | Dolibarr | **No**: decrement at ship-confirm only |
| Bins modelled as warehouses | ERPNext | **No** |
| General route/rule engine (push/pull chains) | Odoo | **No** for v1: costly to build and to explain |
| Forecast-driven reordering (purchasing) | Odoo, ERPNext, Dolibarr | **No**: the shop or an ERP owns purchasing; pick-face min/max replenishment only |

### 1.7 Gaps the domain research could not close

Typical percentage savings from batching; WERC benchmark values (paywalled); normative ABC count frequencies;
location-label layout standards; substitutions; parcel end-of-day close; return grading; 3PL billing. The
dimensional-weight divisors and voice-picking claims rest on Wikipedia only.

---

## 2. Hardware and browser realities

### 2.1 Camera scanning: `BarcodeDetector` support and the WASM ponyfill

**Support matrix** (MDN browser-compat-data,
[BarcodeDetector.json](https://raw.githubusercontent.com/mdn/browser-compat-data/main/api/BarcodeDetector.json)):

| Browser | `BarcodeDetector` |
|---|---|
| Chrome Android 83+ | Supported |
| Chrome desktop 88+ | Partial: "Supported on ChromeOS and macOS only"; silently failed on macOS 13+ before Chrome 113 |
| Edge, Opera | macOS only |
| Firefox | Not supported |
| Safari 17 | Behind the "Shape Detection API" preference |

The API is experimental, secure-context only, and has `getSupportedFormats()`. Formats include `code_128`, `code_39`,
`ean_13`, `itf`, `data_matrix`, `qr_code`, `pdf417` and more
([MDN](https://developer.mozilla.org/en-US/docs/Web/API/Barcode_Detection_API)). **Windows pack stations, Linux and
iPhones therefore need a fallback.**

**Fallbacks**
- [`barcode-detector`](https://github.com/Sec-ant/barcode-detector): MIT; a `BarcodeDetector` polyfill and ponyfill on
  ZXing-C++ WebAssembly (zxing-wasm), with the standard API shape. It registers only if `globalThis.BarcodeDetector` is
  absent. **The `.wasm` is fetched from jsDelivr by default**; `prepareZXingModule` overrides that for a strict CSP or
  offline use, so self-host it.
- [`@zxing/library`](https://github.com/zxing-js/library): Apache-2.0, **in maintenance mode** ("changes are driven by
  contributed patches"). Avoid it for new work.
- Commercial SDKs (Scandit, Dynamsoft, STRICH, Scanbot) exist but were not evaluated. They would be a host choice behind
  the same scan-source interface.

**Expectation.** The camera is a fallback for low volume (one-off receipts, small shops); a dedicated scan engine is the
norm for picking at volume. No published like-for-like benchmark was found; spike W5 measures one.

### 2.2 Keyboard wedge versus intents

- **Zebra DataWedge Keystroke Output** emulates typing
  ([keystroke output](https://techdocs.zebra.com/datawedge/latest/guide/output/keystroke/)).
  - "Send Characters as Events" sends ASCII 32–126 as key events when enabled; the fetched text does not state its
    default (believed off) [unverified]. "Send Control Characters as Events" is off by default, so control characters go
    as a string. The page notes that "web applications listen to these key events using DOM… they do not generate
    Android key press events".
  - ENTER and TAB are sent as key events unless "Send Enter/Tab as string" is set.
  - "Action key character" can turn ASCII Tab, LF or CR into the action key. **GS (0x1D) is not one of the action-key
    options**; it appears in the control-character table as "Group separator".
  - The inter-character delay is 0 ms by default. If delivery errors occur, raise it in 100 ms steps; it hurts
    performance. A separate multi-byte delay targets Unicode issues.
  - On Android 10+ the soft keyboard briefly disappears during a scan, because DataWedge dispatches through its own
    InputMethodService.
  - **Hypothesis for spike W4:** in string mode, characters are committed as text, so the Android keyboard layout should
    not mangle them. The spike also checks whether a GS byte survives into an `<input>` value in Chrome.
- **Intent Output** (`com.symbol.datawedge.data_string`, `label_type`, raw `decode_data`) is delivered to Android apps
  through startActivity, startService or a broadcast. The page does not say a web page can receive it, so **plain web
  apps cannot use it** ([intent output](https://techdocs.zebra.com/datawedge/latest/guide/output/intent/)); it needs a
  native shell or Zebra Enterprise Browser.
- **Profiles.** An app is associated with exactly one profile, and **Profile0** applies to any app without one
  ([profiles](https://techdocs.zebra.com/datawedge/latest/guide/profiles/)). Ship a documented DataWedge profile for
  Chrome or the installed PWA: Enter suffix, symbology identifiers on, a GS replacement if needed. Deployment through
  MDM/StageNow [unverified].
- **Zebra Enterprise Browser.** The TechDocs landing page is archived ("a newer version… available") and the
  `latest/about` page returned 404 ([archived landing](https://techdocs.zebra.com/enterprise-browser/)). Its status is
  unclear; do not depend on it.
- **Honeywell** has equivalent wedge and "data intent" modes [unverified].

### 2.3 The AZERTY problem with desktop HID scanners

- USB and Bluetooth HID scanners send **physical key codes**; the host's keyboard layout turns them into characters. MDN:
  `code` "isn't altered by keyboard layout", and `KeyQ` is the A key on AZERTY
  ([KeyboardEvent.code](https://developer.mozilla.org/en-US/docs/Web/API/KeyboardEvent/code)).
- A scanner configured for a US layout therefore produces, on a Belgian AZERTY host, `&é"'(§è!çà` for the digits, and
  swaps Q↔A and W↔Z.
- Fixes: set the scanner's country keyboard to Belgian; use the scanner's keypad or ALT-code emulation mode; or decode
  from `event.code` in a dedicated scan listener (vendor options [unverified]). The pack-station part of spike W4 must
  cover this.

### 2.4 Design rules for the scan layer (research reading)

- One focused capture element with `inputmode="none"` to suppress the soft keyboard.
- A terminator (Enter) plus a timing heuristic (an inter-key gap of a few ms) to tell a scan from typing.
- Manual entry always accepted as a fallback.
- Parse by **symbology identifier** where the scanner sends one.
- Validate check digits (GTIN, SSCC, location check digits) before any server call.

### 2.5 GS1 barcodes and the Barcode Syntax Engine

**AI formats** (GS1 AI dataset, [GS1_Application_Identifiers.jsonld](https://ref.gs1.org/ai/GS1_Application_Identifiers.jsonld)):

| AI | Title | Format | Needs FNC1 when not last |
|---|---|---|---|
| 00 | SSCC | N2+N18 | no (fixed) |
| 01 | GTIN | N2+N14 | no |
| 02 | CONTENT (GTIN of contained trade items) | N2+N14 | no |
| 10 | BATCH/LOT | N2+X..20 | **yes** |
| 11 | PROD DATE | N2+N6 | no |
| 15 | BEST BEFORE | N2+N6 | no |
| 17 | USE BY / EXPIRY | N2+N6 | no |
| 21 | SERIAL | N2+X..20 | **yes** |
| 30 | VAR. COUNT | N2+N..8 | **yes** |
| 3103 | NET WEIGHT (kg, 3 decimals) | N4+N6 | no |

- The date patterns allow day `00`, which per the GS1 General Specifications means "end of month" (meaning [unverified];
  the specification page exceeded the fetch limit). AI 37 (count of trade items) and 400/410/420 (PO, ship-to GLN,
  ship-to postal code) were truncated in the fetch [unverified].
- **SSCC** is AI (00), 18 digits: "an extension digit, a GS1 company prefix, a serial reference, and a check digit". It is
  created at packing by the shipping party and is common in ASNs
  ([Wikipedia SSCC](https://en.wikipedia.org/wiki/Serial_shipping_container_code); gs1.org returned 403). That makes it
  the natural carton or pallet id for packed shipments when the host has a GS1 company prefix. GS1-128 is marked by FNC1
  in first position ([Wikipedia GS1-128](https://en.wikipedia.org/wiki/GS1-128)).
- **Symbology identifiers** `]C1` (GS1-128), `]d2` (GS1 DataMatrix), `]Q3` (GS1 QR) and `]e0` (GS1 DataBar) tell a parser
  the payload is a GS1 element string [unverified]. Turn them on in the scanner profile.
- **GS1 Digital Link and Sunrise 2027.** Sunrise 2027 is "a global transition from 1D UPC barcodes to 2D barcodes, like
  QR codes"; a 2D code can carry the same GTIN plus Digital Link data
  ([GS1 US Sunrise 2027](https://www.gs1us.org/industries-and-insights/by-topic/sunrise-2027)). A parser must therefore
  accept Digital Link URIs as well as element strings.
- **Parser.** The [GS1 Barcode Syntax Engine](https://github.com/gs1/gs1-syntax-engine) (Apache-2.0) processes AI element
  strings and Digital Link URIs. It is a C library with bindings for **C# .NET, JavaScript via WebAssembly**,
  Java/Kotlin and Swift. Wrap it, or port its AI dictionary into a small table-driven parser; either way keep the
  dictionary as data (Odoo's lesson, §1.5).

### 2.6 Label printing

**Raw printing**
- ZPL goes to "the appropriate TCP port on the printer (default port is 9100)"
  ([JetDirect](https://en.wikipedia.org/wiki/JetDirect)). A .NET server on the same LAN can do that directly. **A browser
  cannot open raw sockets**, and a cloud-hosted server cannot reach a LAN printer.

**.NET tooling**
- [BinaryKits.Zpl](https://github.com/BinaryKits/BinaryKits.Zpl) (MIT): a ZPL builder, a Labelary client and a local
  viewer, with a TcpClient-to-9100 example.
- [Labelary](https://labelary.com/service.html) renders ZPL to PNG or PDF. The free tier allows 3 requests/s and
  5,000/day, "free for any use"; an on-premises option exists. Useful for previews and golden-file tests.
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) (Apache-2.0) encodes Code 128, DataMatrix and QR for PDF labels. The
  repository renders QR only, inside Authorization (§3.8).

**Carrier label formats**
- **Sendcloud v3** parcel documents: `Accept` of `application/pdf` (default), `application/zpl` or `image/png`;
  `paper_size` A4, A5 or A6; `dpi` 72–600. "ZPL isn't affected by the DPI setting, and most carriers use 203"
  ([retrieve parcel document](https://sendcloud.dev/api/v3/parcel-documents/retrieve-a-parcel-document.md)). ZPL is native
  for carriers that support it and converted from PDF otherwise
  ([print your labels](https://sendcloud.dev/docs/shipments/print-your-labels.md)). v2 is in maintenance mode
  ([llms.txt](https://sendcloud.dev/llms.txt)).
- **PostNL** Shipment V4 lets you "select the file type that suits your printer"
  ([shipment v4](https://developer.postnl.nl/integration-with-postnl/api-overview/shipment-v4/)); exact printer types
  [unverified]. **bpost** label formats [unverified].
- Design consequence (research reading): the carrier adapter returns `(bytes, mediaType)` with PDF or ZPL at a stated dpi,
  and the print pipeline routes ZPL to thermal printers and PDF to the PDF path.

**Browser-to-printer bridges**
- **QZ Tray** ([docs](https://qz.io/docs/); licence **LGPL-2.1**,
  [LICENSE.txt](https://raw.githubusercontent.com/qzind/tray/master/LICENSE.txt)): a local app the page talks to over a
  local WebSocket (`qz.websocket.connect()`). Raw printing (ZPL, EPL, ESC/POS and others); pixel printing (PDF, HTML,
  images); serial, USB and HID access. **Silent printing requires signed messages**, which means a server-side signing
  endpoint and a certificate. Pricing exists ("Buy"); the terms are [unverified].
- **Zebra Browser Print**: "USB, Network, or Bluetooth-based printing support" for browser apps; USB and network on
  Windows and macOS, network and Bluetooth on Android; the JS library is available on request
  ([Zebra](https://www.zebra.com/us/en/support-downloads/software/printer-software/browser-print.html)). Licence terms
  [unverified].
- **WebUSB**: experimental, secure context, explicit device permission
  ([MDN](https://developer.mozilla.org/en-US/docs/Web/API/WebUSB_API); [Chrome](https://developer.chrome.com/docs/capabilities/usb)).
  On Windows the printer driver normally claims the device [unverified]. Not a v1 path.
- **`window.print()` of an A6 / 4×6 PDF** always works but shows the print dialog. Chromium's `--kiosk-printing` switch
  suppresses the dialog on a dedicated pack-station PC [unverified].
- **Zebra Link-OS "Weblink"**: the Link-OS SDK's "Java for Webservices" enables printing "from web servers using
  Websockets (Weblink) technology" ([Link-OS](https://techdocs.zebra.com/link-os/)). The printer connects to the server,
  so a cloud server can print without a local agent. The SDK is Java only; a .NET implementation would be a spike.
- **On-premises print agent** (the ShipStation Connect / PrintNode pattern; details [unverified]): a small .NET worker in
  the warehouse pulls print jobs over HTTPS or a WebSocket and writes to port 9100. It is robust and dialog-free and works
  with a cloud server.

**Research recommendation.** A server-side **print job queue** (a job = template + data, or carrier bytes, routed to a
printer and an adapter) with an exactly-once acknowledgement, plus pluggable adapters: `Pdf` (default, zero install),
`Agent` (on-premises worker), `QzTray`, `ZebraBrowserPrint`, `RawTcp` (server on the LAN), and later `PrintNode` and
`Weblink`.

### 2.7 Offline: Background Sync, storage eviction, Safari's 7-day rule

- **Background Sync** (`SyncManager`) is supported in Chrome 49+ and its mirrors, and **not** in Firefox, Safari or
  **Android WebView** ([SyncManager.json](https://raw.githubusercontent.com/mdn/browser-compat-data/main/api/SyncManager.json);
  [Background Sync API](https://developer.mozilla.org/en-US/docs/Web/API/Background_Synchronization_API)). It retries with
  backoff after connectivity returns, and the page may be closed by then
  ([Chrome blog](https://developer.chrome.com/blog/background-sync)). Treat it as an optimisation; the app's own outbox
  must replay on reconnect.
- **Storage** ([MDN storage quotas](https://developer.mozilla.org/en-US/docs/Web/API/Storage_API/Storage_quotas_and_eviction_criteria)):
  best-effort storage is evicted LRU under pressure; persistent storage is evicted only by the user. Chromium and Safari
  auto-grant or deny `navigator.storage.persist()` from interaction history without a prompt; Firefox prompts. **Safari
  deletes script-written storage after 7 days without interaction.** Chromium's quota is up to 60% of disk.
- **The repository's PWA layer** is `NS/pwa/src/provide-spark-service-worker.ts` (Angular service worker). It caches the
  app shell only: "No `dataGroups` for `/spark/**`" (`docs/guide-pwa.md:33-34`), enforced by
  `tools/verify-ngsw-config.test.mjs:90-96`. An offline command outbox is new work, and commerce needs one for the ticket
  scanner (commerce §4.11; spike S9, `docs/commerce_PRD.md:541`).
- **Theory.** Stock with a `≥ 0` invariant cannot be merged freely. Balegas et al. 2015 add escrow-style **bounded
  counters** to enforce numeric invariants such as non-negative stock under eventual consistency
  ([arXiv:1503.09052](https://arxiv.org/abs/1503.09052)).
- **Practical rules for an offline mode** (research reading):
  1. Offline only for **tasks assigned before going offline**. The allocation already reserved the stock: that is the
     escrow.
  2. Every scan or confirmation is a command with a client-generated **idempotency key**. The server applies each key
     exactly once and stores it as the movement id.
  3. The server stays authoritative. A command that would break an invariant (location emptied meanwhile, task
     reassigned) goes to an **exception queue** for a supervisor and is never force-applied.
  4. No offline receiving, counting or adjusting. A count taken offline is an absolute value and conflicts with
     concurrent movements.
  5. Precedent: Sortly ships "Offline Mobile Access" for a simpler domain ([features](https://www.sortly.com/features/)).

---

## 3. Spark fit

### 3.1 Cross-app mechanisms today

`docs/guide-cross-module-sync.md:14-18` lists three:

| Mechanism | How | Evidence |
|---|---|---|
| Module registry | Each app upserts `moduleInformations/{Name}` (URL, DB name, DB URLs, pinned certificate thumbprint) in the shared `SparkModules` database at startup. It also tries to create that database, which needs cluster admin. | `RA/Models/ModuleInformation.cs:14,30`; `R/Services/ModuleRegistrationService.cs:33` (create), `:58-68` (refuses a different thumbprint: check, log, throw), `:79,91` (pins on first registration) |
| ETL deployment | The consumer scans `[Replicated]` types and groups them by source module (`R/Services/EtlScriptCollector.cs:17-56`). It broadcasts `EtlScriptDeploymentMessage` on the message bus (`R/Messages/EtlScriptDeploymentMessage.cs:14`, queue `spark-etl-deployment`). The recipient (`R/Messages/EtlScriptDeploymentRecipient.cs`) looks up the source URL in `SparkModules` on every delivery and POSTs `/spark/etl/deploy`. The owner creates **one** RavenDB ETL task per requesting module, `spark-etl-{Module}`, with one transform per source collection and the script copied verbatim. | `R/Services/EtlTaskManager.cs:25-26,52-60,65-70,80-96`; `R/Extensions/SparkReplicationExtensions.cs:73-95,135` |
| SyncAction write-back | Saves and deletes of a `[Replicated]` type in the consumer trigger `ReplicationInterceptor`, an **after**-interceptor (`IAfterSave`, `R/Services/ReplicationInterceptor.cs:16`). `SyncActionInterceptor` stores a `SparkSyncAction` in a **new session** (`R/Services/SyncActionInterceptor.cs:149-163`). `SyncActionSubscriptionWorker`, a RavenDB data subscription, POSTs it to the owner's `/spark/sync/apply` (`R/Workers/SyncActionSubscriptionWorker.cs:16,36,59-73`). The owner applies it through `IDatabaseAccess` with `PersistentObjectOperation.Sync` (`S/Services/SyncActionHandler.cs:56-63`). ETL then copies the result back into the replica. | as cited |

Smaller facts that bear on the design:
- **Partial updates are honoured at the owner:** it keeps only the attributes named in `Properties`
  (`S/Services/SyncActionHandler.cs:135-141`).
- **The owner applies an update to the version it holds now:** "The replica's change vector means nothing here"
  (`S/Services/SyncActionHandler.cs:43-54`). A write that lands between that read and the save is a conflict, and a
  deleted row is never resurrected.
- **Through the UI, `Properties` names only the changed attributes** (`IsValueChanged`,
  `R/Services/SyncActionInterceptor.cs:50-59`), although `Data` carries every attribute (`:63-67`); the owner applies only
  what `Properties` names. **An entity (programmatic) save names every writable property** of the replica type, and so
  does a UI save in which no attribute is flagged as changed (`:55-59,100`, both through `GetSparkWritableProperties`,
  `:208`).
- **`InitiatorId` carries the editing user**, so the owner can stamp audit fields
  (`docs/guide-cross-module-sync.md:161-164`; `docs/guide-auditing.md:106-121`).
- Retries park and are woken by a sweeper every `FallbackPollInterval` (30 s) (`docs/guide-cross-module-sync.md:103-127,239-262`).

### 3.2 Replication on the Community licence

**ETL is unavailable, and the repository measured it twice**
- "ETL deployment — `LicenseLimitException`; Community has no ETL, Developer does"
  (`docs/endpoints_generator_adoption_plan.md:219`).
- "the repo's `raven-license.log` refuses ETL (500); a RavenDB developer licence deploys it (200). Same test, same code"
  (`docs/multi-host-e2e-plan.md:130-132`). Also recorded in `docs/coverage-handoff-plan.md:134-138` and `:1128-1130`,
  and `docs/issue_460_PRD.md:1331-1334`.
- RavenDB's pages agree. The pricing page ([ravendb.net/buy](https://ravendb.net/buy)) does not list RavenDB ETL for
  Community; for Community it states "3 subscriptions per database & 15 subscriptions per cluster", a 6 GB cluster memory
  cap and a cluster size of 3. The 4.0 licensing matrix lists ETL as "No" for Community and "SQL & RavenDB" for
  Professional and Enterprise ([Ayende, 2017](https://ayende.com/blog/178434/ravendb-4-0-licensing-pricing)). The
  [licensing overview](https://docs.ravendb.net/7.1/start/licensing/licensing-overview) has no feature table; it calls
  Developer "fully featured" and Community "basic production-grade".
- **Production runs Community 7.1.10** (`docs/commerce_PRD.md:329`; server 7.1.10 against client 7.2.6, `:550`).
  **Local development and CI run the Developer licence**: the `localhost:8080` server was measured as Developer
  (`docs/contributions_PRD.md:1090`), and CI and the local test sweep use the Developer licence (`CLAUDE.md`,
  "Running the test suites"). **An ETL design therefore passes every local and CI test and fails in production.**
- The commerce PRD now records this itself: "Transport is not settled" (`docs/commerce_PRD.md:329`), with S18 (a
  Community feature checklist) and S20 (the fulfilment transport on Community constraints) to verify it (`:550`, `:552`).

**Revisions are capped at 2 per document**
- "revisions 1000 > licensed 2" was refused (`docs/identity_provider_platform_PRD.md:391`; `CLAUDE.md:76`;
  `docs/contributions_PRD.md:1088` words it "minimum revisions 1000 exceeds the licensed one 2"). That rules out a change feed built on revisions, and History (which uses RavenDB revisions,
  `libs/history/MintPlayer.Spark.History/README.md:3-6`) as a stock-movement audit trail.

**What works and what fails**

| Replication piece | Works on Community? | Evidence / note |
|---|---|---|
| `SparkModules` registry, module lookup | **Yes** (plain documents). Creating the database needs cluster admin; in production the operator creates databases, and each app certificate holds Admin on its own database only | `R/Services/ModuleRegistrationService.cs:33`; `docs/code-coverage/raven-tls-migration.md:48-59` |
| mTLS module identity (`Module:{CN}` principal) | **Yes** | `apps/Fleet/Fleet/Program.cs:21-46,100-104`; `docs/guide-replication-mtls.md:21-37` |
| ETL deployment message (message bus) | Delivered, but the call it makes fails; retried by the messaging defaults, then dead-lettered | `Msg/SparkMessagingOptions.cs:40,76-85` |
| `/spark/etl/deploy` → `AddEtlOperation` | **No.** `LicenseLimitException`, logged as `ETL_DEPLOY_FAILED`, returns 500 | `R/Services/EtlTaskManager.cs:92,98-106`; `R/Endpoints/EtlDeploy.cs:113-115` |
| Owner → consumer data flow (replicas) | **No** | follows from the row above |
| SyncAction write-back (subscription + HTTP) | **Technically yes.** It costs 1 of the 3 subscriptions per database, and it is registered unconditionally (`R/Extensions/SparkReplicationExtensions.cs:49`), so it holds that slot even when unused. Without ETL there is no replica, so it can only push inserts into a collection the other app owns | `R/Workers/SyncActionSubscriptionWorker.cs:16,36` |

SyncAction delivery is **not** on the message bus: `SparkSyncAction` "Replaces the message bus approach for sync action
delivery" (`R/Models/SparkSyncAction.cs:7`). Only ETL deployment requests use the bus.

**Subscription budget per application database on Community: 3, and 15 per cluster**
- Messaging uses 1: it runs in single-subscription mode by default (`Msg/SparkMessagingOptions.cs:114`;
  `Msg/Services/MessageFeeder.cs:12,17`). Replication's SyncAction worker uses 1 more
  (`docs/messaging_single_subscription_PRD.md:114-120`). Before single-subscription mode, a full-featured app spent all
  three before declaring a queue of its own (same lines).
- The 15-per-cluster budget is shared by every database on the production `coverage-raven` server.
- A subscription created beyond the cap used to die silently while the app reported healthy ("five were silently dead";
  `docs/messaging_single_subscription_PRD.md:37-38`; the production incident in `docs/actions_and_coverage_PRD.md:128-133`).
  The `LicenseLimitException` on subscription create is now fatal (`docs/messaging_single_subscription_PRD.md:323`).

### 3.3 Failure behaviour, retries and ordering

| Concern | ETL (licensed hosts) | SyncAction write-back |
|---|---|---|
| Consumer app down | ETL runs database to database on the server, so **it keeps flowing while the web app is down**, as long as the database is up | n/a (the consumer is the sender) |
| Owner app down | n/a (the source database pushes) | Retries **linearly**, 30 s × attempt, at most 5 attempts (`SW/RetryNumerator.cs:40,46,117-119`; hard-coded `new()` at `R/Workers/SyncActionSubscriptionWorker.cs:20`). Then the action is `Failed`. The sweeper wakes only `Pending` actions (`R/Services/SyncActionRetrySweeper.cs:86`; its comment calls Failed terminal, `:78-79`), so **Failed is final**. Roughly 5–8 minutes of owner downtime loses write-backs |
| Destination database down | RavenDB "fallback mode": retries start at 5 s and double up to `ETL.MaxFallbackTimeInSec` (900 s) ([ETL basics](https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/basics)). Resuming from the last etag is not stated on that page and is assumed from RavenDB ETL's design [unverified] | The local outbox document persists; same budget as above |
| Source module not yet registered | Deployment message retried by messaging (5 attempts: 5 s, 30 s, 2 m, 10 m, 1 h; `Msg/SparkMessagingOptions.cs:78-85`), then dead-lettered until the consumer restarts | Throws `HttpRequestException` and uses the same capped budget. The guide says it is "retried until the owner module registers" (`docs/guide-cross-module-sync.md:245`); **that is wrong** |
| Ordering | Batched; each batch is applied transactionally at the destination. The latest state wins and intermediate states can be skipped (RavenDB docs). Convergent: suited to state, not to event streams | FIFO on first delivery only. Each action is its own document; the subscription takes `Pending` actions and retries are woken by a flag the sweeper sets (`R/Workers/SyncActionSubscriptionWorker.cs:34-37`), so **an older parked action redelivered after a newer one overwrites it** (WF4) |
| Idempotency | State replay is naturally idempotent | Updates use absolute values, so a replay usually lands harmlessly. An insert replay on an existing id goes through `mustExist=false`; the outcome is [unverified] |
| Retry granularity | RavenDB-internal | `FallbackPollInterval`, 30 s by default (`RA/Configuration/SparkReplicationOptions.cs:73`; `docs/guide-cross-module-sync.md:261`) |

### 3.4 Security

**Identity**
- A module's certificate thumbprint is pinned on first registration; a re-registration with a different thumbprint is
  refused (`R/Services/ModuleRegistrationService.cs:58-68`).
- Modes: `Auto` (the default; `Production` outside the Development environment), `Production`, `Development`, and
  `Disabled` (pass-through with a warning per call, for legacy demos only). Development skips the thumbprint check but
  still requires the module to be registered (`docs/guide-replication-mtls.md:21-37`).
- A module principal is an ordinary Spark caller, `Module:{CN}`, governed by `security.json`
  (`apps/Fleet/Fleet/Program.cs:100-104`).
- Behind a TLS-terminating proxy a forwarded-certificate header is supported, and `KnownProxies` is required
  (`R/Authentication/ModuleCertificateForwarding.cs:3-35`).

**Read authorization**
- The owner grants `Replicate/{Collection}` per module, by collection name (`R/Endpoints/EtlDeploy.cs:99`;
  `apps/Fleet/Fleet/App_Data/security.json:107-111`; `apps/HR/HR/App_Data/security.json:78,126`).
- **Residual hole:** the transform is consumer-supplied JavaScript deployed verbatim (`R/Services/EtlTaskManager.cs:65-70`),
  and RavenDB ETL scripts can `load(id)` any source document
  ([Raven ETL](https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/raven)). The earlier mTLS write-up names the pivot,
  as `loadDocument` (`docs/findings-replication-mtls.md:66`), but the fix checks only `SourceCollection`: after the mTLS
  gate, the deploy loop calls only `IsAllowedAsync("Replicate", script.SourceCollection)`
  (`R/Endpoints/EtlDeploy.cs:97-111`). A module granted `FulfilmentOrders` can therefore read `SparkUsers/...` through
  `load()` (WF5).
- The same loop never checks the request's `TargetDatabase` or `TargetUrls`, so the caller decides where the owner's
  server sends the data (noted while compiling this file; details after the table in §4).

**Write authorization**
- Writes go through the type rights at the `IDatabaseAccess` chokepoint (`S/Services/SyncActionHandler.cs:56-63`); an
  E2E test proves the refusal (`E2E/Security/CrossModuleSyncTests.cs:119-133`).
- **Attribute rights do not apply:** "System context (module sync, replication) is not restricted by attribute rights"
  (`docs/guide-authorization.md:122`); the persistent object is built with `AsSystem()`
  (`S/Services/SyncActionHandler.cs:132`). Consequence: a warehouse granted `Edit/FulfilmentOrder` could overwrite the
  address, the lines or the prices, not only the status (WF6).

**Registry trust root**
- The thumbprint pin is enforced only in application code (`ModuleRegistrationService`). Every module needs write access
  to `SparkModules` in order to register, so a compromised module can rewrite another module's
  `ClientCertificateThumbprint` and `AppUrl` directly in the database (`RA/Models/ModuleInformation.cs:30`).
- An operator-managed registry where apps have read-only access would close this, but registration needs a mode that only
  reads. Registration and the ETL scan run in one fire-and-forget startup task: any exception from `RegisterAsync`
  (`R/Extensions/SparkReplicationExtensions.cs:84`) ends it, the catch logs "Error during Spark replication startup", and
  the ETL scan never runs while the host keeps running (`:75-117`, catch at `:113-116`). A refused write to a read-only
  registry would surface there (inference). See WX4.

**Test coverage gaps**
- Thumbprint pinning has unit tests only: "an end-to-end mTLS test … is still missing"
  (`E2E/Security/CrossModuleSyncTests.cs:24-31`).
- No E2E test proves that data actually replicates between two hosts; the multi-host plan's work is "Not started"
  (`docs/multi-host-e2e-plan.md:13-27`).

### 3.5 Limits of the current `[Replicated]` design

**Shape of a declaration**
- **One owner per collection; data flows one way per collection.** "Bidirectional" means two one-way flows over different
  collections: Fleet Cars → HR, and HR People and Companies → Fleet (`docs/guide-cross-module-sync.md:178-206`).
- **`[Replicated]` is compile-time** (`RA/ReplicatedAttribute.cs:9-35`): `AllowMultiple = false`, a single required
  `SourceModule` constant and a required `EtlScript`. A generic library therefore cannot be told the source module's name
  by configuration, and it cannot pull the same contract from several shops (3PL). Write-back always goes to the single
  `SourceModule` (`DispatchAsync(attr.SourceModule, …)` in `R/Services/SyncActionInterceptor.cs`).
- **One replicated type per source collection per consumer.** Transforms are named by source collection inside one task
  (`R/Services/EtlTaskManager.cs:65-70`); two types over the same collection would produce duplicate transform names,
  which RavenDB probably refuses [unverified].

**RavenDB ETL semantics** ([Raven ETL 7.1](https://docs.ravendb.net/7.1/server/ongoing-tasks/etl/raven);
[Raven ETL 7.0](https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/raven))
- Loading into the **same collection preserves the id**, and updates happen in place. Loading into another collection
  generates ids like `employees/1-A/people/0000…-A`, and **every update deletes and replaces** the old copy under a new
  id. So a warehouse that renames the collection cannot keep stable references to orders, and one that keeps the name
  makes two shops' HiLo ids (`FulfilmentOrders/1-A`) **collide** in one warehouse database.
- **Source deletes propagate by default**; `deleteDocumentsBehavior` (or its per-collection variant) can veto them. Spark
  exposes no such option, so a shop that deletes a fulfilment order would remove it from the warehouse mid-pick.
- Revisions are not sent. "All documents loaded in a single ETL run are sent in one batch and processed transactionally in
  the destination."
- Replica documents are overwritten or re-loaded on every source change, and the page does not say whether target-only
  fields survive. Never store warehouse fields on a replica document.

**Removal, naming, nested values, stale URLs**
- **ETL tasks are never removed.** `EtlDeploymentResult.TasksRemoved` (`RA/Models/EtlDeploymentResult.cs:11`) is never
  set, only read in a log line (`R/Messages/EtlScriptDeploymentRecipient.cs:44`); `EtlTaskManager` can only create or
  update a task; and a consumer with no `[Replicated]` types returns early
  (`R/Extensions/SparkReplicationExtensions.cs:93-96`). A withdrawn or off-boarded consumer keeps receiving data,
  personal data included (WF7).
- **The two sides compute collection names differently.** The consumer uses a naive pluralizer
  (`R/Services/EtlScriptCollector.cs:61-72`, duplicated in `SyncActionInterceptor`); the owner uses RavenDB's conventions
  (`S/Services/SyncActionHandler.cs:277`). They disagree on irregular plurals (`Person` gives `Persons` against
  `People`), so `SourceCollection` must always be set.
- **Nested values in write-back are untested.** On the consumer side, `NormalizeValue` turns a `JsonElement` array or
  object into its JSON **text** (`R/Services/SyncActionInterceptor.cs:170-184`), and no replication test covers AsDetail or
  array write-back. Suspected to break shipment and line write-back (WF8).
- Deployment messages carry the consumer's database URLs captured at send time
  (`R/Extensions/SparkReplicationExtensions.cs:135`).

### 3.6 How Fleet and HR use it

- **Declarations.** HR replicates Fleet's Cars (`apps/HR/HR.Library/Replicated/Car.cs`). Fleet replicates HR's People and
  Companies (`apps/Fleet/Fleet.Library/Replicated/Person.cs`, `Company.cs:9-21`).
- **Grants.** Fleet gives `Module:HR` `Replicate/Cars` and `ReadEditNew/Car`
  (`apps/Fleet/Fleet/App_Data/security.json:13,59-64,107-111`). HR gives `Module:Fleet` `Replicate/Companies`,
  `Replicate/People` and `ReadEditNew/Person` (`apps/HR/HR/App_Data/security.json:13,78,126,132`).
- **Configuration.** `Spark:Replication` holds ModuleName, ModuleUrl (5003/5005), `SparkModulesUrls` =
  `http://localhost:8080` and the database name `SparkModules` (`apps/Fleet/Fleet/appsettings.json:19-24`; HR the same).
  `AssembliesToScan` is set in code (Fleet `Program.cs:53`, HR `Program.cs:85`).
- **Fleet's Kestrel** asks for a client certificate without requiring one, defers chain validation to the pinned
  thumbprint (`apps/Fleet/Fleet/Program.cs:21-46`), and turns certificate prompting off in Development
  (`appsettings.Development.json:9`).
- **E2E.** `CrossModuleE2EFixture` runs Fleet in Development certificate mode
  (`E2E/_Infrastructure/CrossModuleE2ECollection.cs:26-35`). It proves the authorization chain, not an actual data flow.

### 3.7 The question asked: FulfilmentOrder to the warehouse, status and stock back

- **On a licence with ETL** (Developer, Professional, Enterprise): yes, with conditions. The warehouse declares a replica
  of `FulfilmentOrders`, and the shop grants `Replicate/FulfilmentOrders` to `Module:Warehouse`. Status can come back two
  ways:
  - (a) SyncAction write-back into the shop's order: unreliable today (WF1–WF4, WF6, WF8);
  - (b) a second ETL in the other direction (`Shipments`, `StockLevels` owned by the warehouse): the clean option, with
    one writer per document, and it keeps flowing while either web app is down.

  The generic library still needs WX2, because a compile-time `SourceModule` cannot be set by configuration.
- **On Community (production): no.** No replicas exist. §5 compares the transports that remain.

### 3.8 Reuse and gaps for warehouse needs

| Need | What exists | Verdict / gap (research reading) |
|---|---|---|
| Handheld app, installable | Every app is a PWA: manifest, icons, `@mintplayer/ng-spark/pwa`, `provideSparkServiceWorker()` (`docs/guide-pwa.md:3-4,11-58`). The service worker exists in production builds only (`:57-58`) | Fine for the app shell. External login inside an installed app has known caveats, for example Firefox on Android (`:146-164`) |
| Offline data | **None.** "No `dataGroups` for `/spark/**`" (`docs/guide-pwa.md:33-34`), enforced by `tools/verify-ngsw-config.test.mjs:90-96`. Commerce's offline scanner is ticket-specific: a hashed list plus a sync that flags double entries (commerce §4.11, spike S9) | Needs **WX7**: an IndexedDB command queue with idempotent replay and a conflict report, shared with the commerce scanner. Online-first with a queue for short disconnects; **no offline allocation** |
| Barcode / QR **scanning** | **Nothing.** A grep of `libs`, `apps` and `tools` for barcode, `BarcodeDetector`, `getUserMedia` and zxing finds no scanning code. `@mintplayer/ng-bootstrap` 22.25.0 has entry points such as `signature-pad`, `otp-input` and `datatable`, but no scanner or camera | **WX5**: keyboard-wedge input, camera input through `BarcodeDetector` with a lazily loaded fallback decoder, and a GS1 parser (AIs 01, 10, 17, 21, 00). Shared with the commerce scanner PWA; kept out of `Ticketing.Tickets` so the warehouse does not depend on ticketing |
| QR / barcode **rendering** | `SparkQrCodeRenderer`: QR only, SVG, QRCoder 1.8.0, inside **Authorization** (`Auth/Identity/SparkQrCodeRenderer.cs:17-44`; csproj `:69`), registered only by `AddAuthentication` (`Auth/Extensions/SparkAuthenticationExtensions.cs:91`) | No 1D codes (Code 128, GS1-128, EAN-13) and no DataMatrix; bin, LPN and SSCC labels need them (**WX6**) |
| PDF / labels / printing | **No PDF library in `libs/`.** SeoTester prints reports through headless Chromium (`page.PdfAsync`, `apps/SeoTester/SeoTester/Reports/ReportPdf.cs:51,124`) using the `seo-chromium` sidecar (`docs/code-coverage/raven-tls-migration.md:28`). Commerce needs PDFs too (invoices §4.8, tickets §4.11, recipient locale X14 `docs/commerce_PRD.md:495`) and has not chosen a library | **WX6**, shared with commerce: barcode generation, a PDF rendering seam (HTML→PDF through Chromium or a .NET PDF library, the host's choice), label templates (4×6 PDF), print delivery (browser print, print-agent adapters, raw ZPL passthrough for carrier labels) |
| Live pick queues | `/spark/live`: `"live": true` per type or query (`docs/guide-live-updates.md:13-36`). With the default `Source = Both`, the Changes API also sees committed writes from other processes, replica applies included (`:29,55,72-73`); it is "**not** a data subscription: it does not count against the Community licence's three subscriptions" (`:66-67`). Throttle 0.5 s; `MaxSocketsPerUser` 5, per user or else per IP (`:51-58`) | Fits. Cost: **one re-evaluation per viewer per change** (`:31-32`). Shared device accounts would hit the 5-socket cap. Commerce X2's private per-identity stream (`docs/commerce_PRD.md:479`) pushes a task to one device |
| Waves, cut-offs, background work | Cron with 6 fields, one run per occurrence across the cluster (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:240-272`). Messaging: FIFO per queue name (`docs/messaging_single_subscription_PRD.md:499`); outbox atomic with the session (`Msg/IMessageOutbox.cs:7-9,19`); durable after-commit interceptors (`A/Interceptors/SparkCommittedInterceptors.cs:3-19`); checkpointed recipients (messaging README) | Fits: scheduled wave release before carrier cut-off, carrier label calls with retries, replenishment triggers, reconciliation |
| Short leases (a picker holds a pick list; freeze during a count) | `IExpiring` is swept at best every 36 h and the floor is enforced (`docs/guide-expiration.md:55,69-77`). Compare-exchange leases exist (`MessagingLease`, `UserStore`) | Use commerce **X12**, the generic claims primitive (`docs/commerce_PRD.md:489`), for leases, tote/LPN uniqueness and idempotency keys |
| Inventory engine (commerce §4.3) | Quotas, shards, holds with `expiresAt`, claims, seats; invariants I1–I5 (`docs/commerce_PRD.md:203-210`) | **A different problem.** Warehouse stock is per (location, SKU, lot, status); allocation picks specific bins and lots (FEFO/FIFO, pick path) at low per-SKU concurrency, and reservations last until picked or cancelled. **Reuse:** X12, the OCC retry pattern (`libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77`), the invariant style and an S1-style randomised concurrency spike. **Interface:** warehouse stock feeds commerce's capacity-source extension point (coupling rule, `docs/commerce_PRD.md:184`). **Handshake:** see §6.4 |
| Carrier labels at packing | At research time the carrier adapters sat under `Commerce.Shipping`, which depended on Checkout; labels are printed at packing when a warehouse app is used (`docs/commerce_PRD.md:314`) | **WX12**: carrier adapters without a Checkout dependency. The commerce package map now states "No Checkout dependency" for `Commerce.Shipping` (`docs/commerce_PRD.md:166`) |

### 3.9 Model fit

**Locations and bins: separate documents, not AsDetail**
- AsDetail values are embedded in the parent document (`docs/guide-asdetail-attributes.md:522-553`). A warehouse document
  with 10⁴–10⁵ bins would be huge and a single concurrency hot spot.
- Model (research reading): `Location` documents (warehouse, zone, aisle, rack, level, bin) with a `ParentId`
  (`[Reference]`), a materialized `Path` of ancestor ids, a human `Code`, a `PickSequence` (int, for serpentine routing)
  and flags (pickable, storage class, capacity); natural ids through `IHasNaturalId`
  (`libs/model/MintPlayer.Spark.Model/IHasNaturalId.cs:41`).
- Child lists as declared sub-queries (`Custom.*` with `args.EnsureParent`, as in
  `apps/CodeCoverage/CodeCoverage/Actions/RepositoryActions.cs:299`).
- **Gap:** the reference picker is flat. "A reference query is flat: no node has children"
  (`NS/po-form/src/reference-query-tree-select-provider.ts:35-38`), although ng-bootstrap's tree-select supports
  `loadChildren` (WX9).

**Stock: one document per (location, SKU, lot, status)**
- Fields `OnHand` and `Allocated`, as base-unit integers (or decimals for weighed items).
- **Not RavenDB counters.** Counters cannot decrement conditionally, so they cannot guarantee "no negative stock" or
  `Allocated ≤ OnHand`. Spark uses counters only for retry counts (`SW/RetryNumerator.cs:68`).
- Creating a record on first putaway races (natural-id probe, then store); commerce X3, atomic natural-id create
  (`docs/commerce_PRD.md:480`), fixes that race.

**Concurrency on stock moves**
- Spark's persistent-object pipeline uses optimistic concurrency per document (`docs/guide-concurrency.md:12-73`): right
  for master data, wrong for moves.
- A move, pick, putaway or adjustment is a domain service, exposed as a custom action, that writes the source and target
  stock records, an immutable `StockMovement` and the task state in **one session** with
  `OptimisticConcurrencyMode.Writes`, retried on `ConcurrencyException` (the `SparkQuotas.cs:46-77` pattern). On a cluster,
  use cluster-wide transactions (precedent: `TransactionMode.ClusterWide` in `Auth/Identity/UserStore.cs:206-208`). Cluster-wide transactions give ACID
  across the database group via Raft and create atomic guards; the page lists only document PUT/DELETE and
  compare-exchange as operations ([cluster transactions](https://docs.ravendb.net/7.1/client-api/session/cluster-transaction/overview)),
  another reason to keep stock in plain documents.
- Stock records are read-only in the UI (no Edit right); corrections are explicit adjustment actions with reason codes.
- Concurrency is modest: warehouse rates are hundreds of picks per hour per picker. Spike WS4 measures hot pick faces.

**Movement history: an explicit append-only ledger**
- **Not History**, which uses RavenDB revisions (`libs/history/MintPlayer.Spark.History/README.md:3-6`) capped at 2 per
  document on Community (§3.2). High-churn stock records would also bloat revisions.
- `StockMovement` documents with from/to, SKU, lot, quantity, reason, reference, user, device and timestamp, and natural
  ids by period. Stamp user and device explicitly: raw session writes are not audit-stamped
  (`docs/guide-auditing.md:123-131`).
- Balances for reconciliation come from map-reduce indexes (precedent:
  `libs/moderation/MintPlayer.Spark.Moderation/Indexes/ModerationIndexes.cs`) and are never used for allocation decisions,
  because they are stale. Plugin indexes ship with `AddIndexesFrom`
  (`libs/moderation/MintPlayer.Spark.Moderation/SparkModerationExtensions.cs:79`); the rule "indexes only in the
  application" is about app entity libraries using `[GenerateIndex]`, not framework plugins.
- Retention is a host setting; the expiry sweep is not suitable.

**Units of measure**
- The model's value types are string, number, decimal, boolean, datetime, date, guid, color, TranslatedString and
  AsDetail (TimeOnly and TimeSpan fall back to string); none carries a unit (`A/Model/SparkModelShape.cs:191-216`).
- Store base units. Packaging levels (inner, case, pallet) are per-item conversion factors that apply only at input and
  display, so scanning a case barcode books 24 eaches.
- **WX8:** a `Quantity` value type (amount + unit) on the same value-type infrastructure as commerce X4 `Money`
  (`docs/commerce_PRD.md:481`).

**Lots, expiry, serials**
- `DateOnly` maps to `date` (`A/Model/SparkModelShape.cs:206`); using it for expiry avoids the `DateTimeOffset` pitfalls.
- A `Lot` document holds expiry, status (released, quarantine, recalled) and FEFO order. GS1 AI (17) and (10) parsing comes
  from WX5.
- Serials captured at pack time on the parcel, not tracked as stock (open question WQ4 at research time).

**Tenancy (3PL)**: `IOrganizationOwned` (`libs/organizations/MintPlayer.Spark.Organizations/IOrganizationOwned.cs:25-29`)
per stock owner. Roles (picker, packer, supervisor) through commerce X8 custom roles (`docs/commerce_PRD.md:485`).

**Devices**: Spark already has the device authorization grant
(`libs/identity_provider/MintPlayer.Spark.IdentityProvider/Endpoints/DeviceAuthorization.cs`) and personal access tokens
(`Auth/Agents/PersonalAccessTokens.cs`). Commerce plans an event-scoped device token for scanners (commerce §4.11).

### 3.10 Demo app, E2E and deployment facts

**App**
- `apps/Warehouse` with `Warehouse`, `Warehouse.Library` and `Warehouse.Tests`. Ports **https 5019 / http 5020** are
  free: every launchSettings file uses 5003–5014 or 5200/5201, and commerce takes 5015/5016 (`docs/commerce_PRD.md:439`).
- Database `SparkWarehouse`, cookie `.SparkAuth.Warehouse`, ModuleName `Warehouse`. The commerce demo would produce
  `fulfilment-orders` and `catalog-items` and consume `fulfilment-status`, `shipments` and `stock-levels`.

**New-app checklist** (`docs/commerce_research.md:2222-2246`)
- Root `package.json` workspaces and `MintPlayer.Spark.slnx`.
- `.github/workflows/pull-request.yml`: the build list (`:101`) and the verify loops (`:112`, `:137`).
- `tools/verify-ngsw-config.test.mjs`: the app list at `:64-66`.
- `tools/test-local.mjs` `E2E_APPS` (`:33`; today `Fleet, QnA, HR, SparkId`).
- `regenerate.yml`, route snapshots, `modelHashes.json` and `securityPosture.txt`.

**Local two-app replication**
- Both apps run on `localhost:8080` with a shared `SparkModules` database, as Fleet and HR already do
  (`apps/Fleet/Fleet/appsettings.json:15-24`). In Development, certificate mode `Auto` resolves to Development; Fleet's
  Kestrel and module-authentication wiring is the template (`apps/Fleet/Fleet/Program.cs:21-46,100-104`).
- The local server holds the Developer licence, so **ETL would work locally and fail in production**. If ETL is
  configured, a startup check must refuse a licence without ETL (WF10).

**E2E**
- One collection fixture with two hosts; the precedent is `SparkIdFleetE2EFixture`
  (`E2E/_Infrastructure/SparkIdFleetE2ECollection.cs`).
- The embedded RavenDB server is one per test process (`libs/testing/MintPlayer.Spark.Testing/SparkEmbeddedServer.cs:1-24`),
  so both databases already share a server.
- Remaining work: give both hosts the **same** `SparkModules` database (today it is per host:
  `E2E/_Infrastructure/FleetTestHost.cs:43`), and generalise the host (multi-host plan H1/H2, not started:
  `docs/multi-host-e2e-plan.md:13-27`). Wait by polling point-loads under a failure bound, never with a stale query (plan
  R2, `:146-185`).
- An ETL transport also needs the Developer licence in the test driver: CI has it; locally set `RAVENDB_LICENSE` as
  `CLAUDE.md` describes. The missing end-to-end mTLS test (`E2E/Security/CrossModuleSyncTests.cs:24-31`) fits the same
  fixture.

**VPS, if the demo were deployed**
- The warehouse database would go on the shared `coverage-raven` server (Community 7.1.10), which forces a non-ETL
  transport. Each app certificate holds DB Admin on its own database only
  (`docs/code-coverage/raven-tls-migration.md:48-54`), and the operator creates databases, `SparkModules` included
  (`:58-59`).
- Subscriptions: messaging takes 1 per database (+1 while the SyncAction worker stays registered) against 3 per database
  and 15 per cluster.
- About 3 GB of RAM is available on the VPS (`docs/coverage_commit_graph_PRD.md:48`): enough for one more ASP.NET app,
  not for a second RavenDB.
- Module mTLS through Traefik needs `passTLSClientCert` plus `KnownProxies`.

---

## 4. Replication defects found (WF1–WF10)

Read-only findings in code that ships today. Each needs a red test before a fix (spike WS3). The commerce PRD carries them
as F12–F18 (`docs/commerce_PRD.md:514-520`): F12 = WF1, F13 = WF2, F14 = WF3, F15 = WF4, F16 = WF5, F17 = WF6,
F18 = WF7–WF10.

| # | Defect | Evidence | Severity |
|---|---|---|---|
| WF1 | **A refused write-back is recorded as Completed.** Each `SparkSyncAction` carries exactly one action. Any refusal (rights, validation, "deleted", a **transient owner conflict**) is caught by one generic `catch` and returned as 207, and the worker counts every 2xx as success (`IsSuccessStatusCode`). The E2E test confirms that a refusal returns 207 | `R/Services/SyncActionInterceptor.cs:149-163`; `R/Endpoints/SyncApply.cs:149-173`; `R/Workers/SyncActionSubscriptionWorker.cs:75-85`; `E2E/Security/CrossModuleSyncTests.cs:127-130` | Correctness: silent divergence |
| WF2 | **Dispatch is not durable.** An `IAfterSave` stores the sync record in a fresh session after the commit. Spark's own rule: "work that must eventually happen belongs in a durable interceptor", and `IAfterSaveCommitted` already writes its outbox entry in the save's own commit | `R/Services/ReplicationInterceptor.cs:16`; `R/Services/SyncActionInterceptor.cs:161`; `A/Interceptors/SparkInterceptors.cs:94-95`; `A/Interceptors/SparkCommittedInterceptors.cs:3-19` | Correctness |
| WF3 | **The retry budget is about 5–8 minutes and Failed is final.** Neither the budget nor re-driving is configurable. The guide says "exponential backoff" (`docs/guide-cross-module-sync.md:243`) and "until the owner registers" (`:245`) | `SW/RetryNumerator.cs:40,46,117-119`; `R/Workers/SyncActionSubscriptionWorker.cs:20,113-116`; `R/Services/SyncActionRetrySweeper.cs:78-79,86` | Reliability |
| WF4 | **Retries reorder writes to the same document**, so an older parked update overwrites a newer one. There is no per-document sequence or version guard | `R/Workers/SyncActionSubscriptionWorker.cs:34-37`; `S/Services/SyncActionHandler.cs:43-54` (applies to the current version) | Correctness (needs a red test) |
| WF5 | **ETL `load()` pivot past `Replicate/{Collection}`.** The consumer's script is deployed verbatim and can load any source document; the authorization checks only the source collection | `R/Services/EtlTaskManager.cs:65-70`; `R/Endpoints/EtlDeploy.cs:97-111`; `docs/findings-replication-mtls.md:66`; [Raven ETL](https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/raven) (`load(id)`) | **Security** |
| WF6 | **Module sync ignores attribute rights**, so an owner cannot limit a module to its status fields | `docs/guide-authorization.md:122`; `S/Services/SyncActionHandler.cs:132` | Security / design |
| WF7 | **ETL tasks are never removed** when a consumer stops replicating. `TasksRemoved` is never set | `R/Services/EtlTaskManager.cs:22-109`; `RA/Models/EtlDeploymentResult.cs:11`; `R/Messages/EtlScriptDeploymentRecipient.cs:44`; `R/Extensions/SparkReplicationExtensions.cs:93-96` | Privacy |
| WF8 | **Nested values (arrays, AsDetail) in write-back are serialized as JSON text.** Suspected; confirm red | `R/Services/SyncActionInterceptor.cs:170-184` | Correctness |
| WF9 | **Guide drift** in `docs/guide-cross-module-sync.md`: `:124` says the write is made "Instead of saving locally", but the code saves locally first (the interceptor forwards a committed write, and a failed dispatch "leaves the local write standing": `R/Services/ReplicationInterceptor.cs:7-15`); `:243` and `:245` misstate the retry behaviour (WF3); `:270-272` still point to `Demo/...` (the folder is now `apps/`) | as cited | Docs |
| WF10 | **No licence check for ETL.** On Community the deploy fails at run time with a generic 500 and a dead-lettered message, and the consumer's registration-and-deployment step runs fire-and-forget, so a failure there is only logged. Compare subscriptions, where a licence failure was made fatal with an actionable message (`docs/messaging_single_subscription_PRD.md:323`) | `R/Services/EtlTaskManager.cs:98-106`; `R/Endpoints/EtlDeploy.cs:115`; `R/Extensions/SparkReplicationExtensions.cs:75-117` | Operability |

**Noted while compiling this file** (not in the sweep, no id, not assessed): the deploy endpoint never checks the
request's `TargetDatabase` or `TargetUrls` against the registry (`R/Endpoints/EtlDeploy.cs:97-111`). The consumer fills
them from its own store (`R/Extensions/SparkReplicationExtensions.cs:135-147`), and they become the ETL connection string
(`R/Services/EtlTaskManager.cs:52-60`). So an authenticated module decides where the owner's server sends the granted
collection, and with WF7 the task keeps running after the module goes away (inference). Research reading: it fits WX4,
next to WF5 and WF7.

---

## 5. Cross-app transport options compared

### 5.1 The options

| Option | Community | New RavenDB subscriptions | App boundary / authz | Consumer down | Producer down | Replay / idempotency | Ordering | Bootstrap | New code |
|---|---|---|---|---|---|---|---|---|---|
| **A. Outbox + HTTPS push** (durable outbox in the producer's commit, module-authenticated POST, versioned upsert) | Yes | **0** (reuses messaging's 1) | Producer-declared projection; `Module:{Name}` principal; no consumer JavaScript | Messages park and retry; a budget is needed, then dead-letter (D repairs) | Nothing is produced; the outbox survives a restart | At-least-once; a version guard makes it idempotent | Irrelevant with a version guard | Needs a snapshot (D) | Medium |
| **B. Pull a change feed** (consumer polls `?since=cursor`) | Yes | 0 | Producer-declared; module principal | Resumes from its cursor without loss | Consumer retries | Idempotent by version | By cursor. **Gaps** when commits finish out of order or the index is stale; `@last-modified` needs an overlap window; a revision-based feed is impossible (2 revisions per document on Community) | Natural (cursor 0) | Medium-high (a correct cursor) |
| **C. Subscription on the other app's database** | Yes | **+1 in the source database** (3 per database, 15 per cluster) | **Breaks per-app isolation**: the warehouse certificate needs access to the shop database, while production gives each certificate Admin on its own database only (`docs/code-coverage/raven-tls-migration.md:48-54`). Bypasses Spark's row security and redaction and couples to the internal schema | Resumes from its change vector | n/a (database to database) | At-least-once with acknowledgement | Etag order per batch, latest state | Natural | Low-medium |
| **D. Periodic snapshot + delta** (digest compare, fetch the differences) | Yes | 0 | Producer-declared | Repaired at the next cycle | Retried next cycle | Idempotent | n/a | Natural | Low-medium; latency = the period |
| E. Reuse SyncAction as push | Yes, after fixes | +1 per database (already counted) | Owner-applied writes through its pipeline; no attribute rights (WF6) | Capped (WF3) | n/a | Weak (WF4) | Reorders (WF4) | None | Fix WF1–WF4, WF6, WF8 |
| F. Buy an ETL-capable licence | No (Professional and up) | 0 | Consumer JavaScript (WF5) | Keeps flowing database to database | n/a | State-based | Latest state | Natural | Low (plus WX2 and the WF fixes) |

### 5.2 Research recommendation: A + D, "push for latency, reconcile for truth"

A new transport inside `MintPlayer.Spark.Replication`, with RavenDB ETL kept as an optional transport chosen in
configuration. The PRD's decision log records what was decided, including how ETL and write-back fit in.

1. **Producer-declared contracts (C#, versioned).** For example
   `Publish<FulfilmentOrder, FulfilmentOrderV1>("fulfilment-orders", version: 1, map)`. Authorization reuses the existing
   verb, now naming the contract: `Replicate/fulfilment-orders` for `Module:Warehouse`. The consumer sends no JavaScript,
   which closes WF5 by construction.
2. **Change capture inside the commit.** An `IBeforeSave` stamps `ReplicationVersion` (+1) in the same session; Spark's
   save writes with the expected change vector (`docs/guide-concurrency.md:43-60`), so the counter is monotonic per
   document. An `IAfterSaveCommitted` / `IAfterDeleteCommitted` enqueues the push, and its outbox message is stored in the
   save's own commit (`A/Interceptors/SparkCommittedInterceptors.cs:3-7`; Messaging is already required by Replication).
   Raw session writes are not captured (`docs/guide-auditing.md:123-131`): library code calls
   `IReplicaPublisher.MarkChanged(session, id)`, and D catches the rest.
3. **Delivery.** One messaging queue per consuming module. The handler **loads the current document**, so a burst of
   writes coalesces into one push of the latest state. It POSTs `{contract, items:[{id, version, changeVector, payload or
   deleted}]}` to the consumer's `/spark/replicas/apply` with the module certificate, through the same
   `IReplicationHttpClientProvider` the current code uses (`R/Services/ReplicationHttpClientProvider.cs`). An unreachable
   peer or a 5xx raises `RetryAfterException`, where five parks count as one attempt (`MsgA/RetryAfterException.cs:3-18`)
   and the budget is set per queue (`Msg/Services/MessageBus.cs:122`). A 4xx raises `NonRetryableException` and is
   dead-lettered with a reason, visibly.
4. **Apply at the consumer.** An item is applied only if `version > stored.version`; a tombstone is remembered so that a
   late older update cannot resurrect a deleted document. Replica documents are read-only in the consumer (Edit, New and
   Delete are never granted). In the same session a durable `IReplicaChanged<T>` is enqueued that compares the previous
   and the new state, so a transition is an event derived on the consumer side and the order of pushes does not matter.
5. **Reconciliation (D).** A consumer cron job runs once per occurrence across the cluster
   (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:240-272`) and at startup. It pages
   `GET /spark/replicas/{contract}/digest?after=&take=` for (id, version, changeVector) and fetches whatever differs; the
   change vector catches raw writes that did not bump the version. Bootstrap is a full reconciliation, and so is recovery
   after restoring a backup.
6. **Reverse direction.** The same mechanism runs with the warehouse as producer (`fulfilment-status`, `shipments`,
   `stock-levels`, `return-receipts`). No write-back into the other app's documents is needed: a shop-side request such
   as cancelling an order is shop state (`CancelRequested` on the fulfilment order), and the warehouse acknowledges it in
   its own status document. This matches the owner's wording, "ETL jobs … consult information from the other app",
   which describes reading in both directions.
7. **Stock levels change on every pick.** Push them as a dirty set flushed every 1–2 s by a 6-field cron rather than once
   per pick. The outbox refuses `DeduplicationKey` (`Msg/IMessageOutbox.cs:14-17`), so coalescing has to happen when the
   handler loads the current state.

The commerce PRD records this recommendation as the warehouse research's and leaves the decision to the warehouse PRD
(`docs/commerce_PRD.md:321-329`).

### 5.3 Failure modes of the recommended design

| Failure | Outcome |
|---|---|
| Consumer app down for hours | Pushes park and retry within the per-queue budget, and the backlog drains on recovery. Messages past the budget are dead-lettered, and the next reconciliation repairs them. The consumer's read-only replicas stay available |
| Producer app down | No changes are produced, because writes go through it. The consumer's reconciliation fails and retries next period. The warehouse keeps picking the orders it already has |
| Crash after commit, before push | The outbox message was stored in the same commit and is delivered after restart (at least once) |
| Crash at the consumer after apply, before replying 200 | The resend hits the version guard, does nothing and returns 200 |
| Duplicate or replayed delivery | Version guard; tombstones |
| Out-of-order delivery | The stale version is dropped. Events come from comparing states, not from message order |
| Poison payload or schema mismatch | 4xx, dead-lettered with its reason and an alert. The digest mismatch persists until it is fixed |
| Contract evolution | `fulfilment-orders/v1` and `/v2` run side by side; the consumer declares which versions it reads |
| Write that bypasses the pipeline (patch, raw store) | Caught by reconciliation through the change vector |
| TLS terminated at Traefik (VPS) | Forwarded client certificate with `KnownProxies` (`R/Authentication/ModuleCertificateForwarding.cs:3-35`), or app to app on the internal Docker network with a message signature (the HMAC strategy of commerce X5, `docs/commerce_PRD.md:482`) |
| Licence and resources | No ETL, **no new subscriptions**, no cross-database certificates. Works when the two apps use different RavenDB servers. The RAM cost is one HTTP client |

### 5.4 Why not the others (research reading)

- **B:** the cursor is the hard part on RavenDB (commit order, index staleness); D gets the same robustness more simply.
- **C:** it breaks isolation and authorization, and spends the source database's last subscription slot.
- **E:** it needs ETL for its replicas anyway, and its command semantics invite the problem WF6 describes.
- **F:** it needs a licence production does not have, and still needs WX2 and the WF fixes. Keep it as an optional
  transport with a startup licence check (WF10).

---

## 6. Shop↔warehouse integration semantics

### 6.1 Reference vocabularies

- **EDI X12** (Stedi reference): [940](https://www.stedi.com/edi/x12/transaction-set/940) Warehouse Shipping Order (the
  depositor asks the warehouse to ship; it can also confirm, modify or cancel a previous order: W05 order id, N1 parties,
  W66 carrier, W01 lines); [945](https://www.stedi.com/edi/x12/transaction-set/945) Warehouse Shipping Advice ("shipment
  was made": W12 lines, W27 carrier, MAN marks/tracking); [943](https://www.stedi.com/edi/x12/transaction-set/943) /
  [944](https://www.stedi.com/edi/x12/transaction-set/944) stock transfer shipment / receipt advice (the inbound ASN and
  receipt); [846](https://www.stedi.com/edi/x12/transaction-set/846) Inventory Inquiry/Advice;
  [947](https://www.stedi.com/edi/x12/transaction-set/947) Warehouse Inventory Adjustment Advice;
  [180](https://www.stedi.com/edi/x12/transaction-set/180) Return Merchandise Authorization and Notification.
- The proposed contract maps 1:1: FulfilmentOrder ≈ 940; WarehouseShipment ≈ 945; ExpectedReceipt / Receipt ≈ 943/944;
  StockLevel ≈ 846; adjustments ≈ 947; RMA / return receipt ≈ 180.
- Shopify's FulfillmentOrder request and cancel handshake, and ShipBob's "cancel only before picked" (§1.5).

### 6.2 Ownership rules that follow from replication

The domain sweep wrote these for ETL; §3.2 shows production has none. They hold for any state-replication transport,
including §5's push and reconcile (inference).
1. **Replicas are read-only.** Warehouse working state (allocations, tasks) lives in warehouse-owned documents keyed by
   the replicated id (natural ids).
2. **Cancellation is a status change, never a delete.** A delete would make the order vanish from the warehouse mid-pick
   (ETL deletes propagate by default, §3.5; in the HTTP design a tombstone deletes the replica).
3. **Assume no ordering across collections.** Each transformation or contract is processed independently (inference;
   spike W3 observes it). Numbers that must be consistent with each other travel **in one document**.
4. **Each fact has one owner**, with flows in both directions: the shop owns `FulfilmentOrder` (including
   `CancellationRequested`) and `ExpectedReturn`; the warehouse owns `FulfilmentProgress`, `WarehouseShipment`,
   `WarehouseStockLevel` and `ReturnReceipt`.
5. Keep stock in plain documents, not counters (§3.9).

### 6.3 Order release

- **Release** means the order becomes eligible for allocation and picking. Policies: **Immediate** (the default for small
  shops); **scheduled waves** before carrier cut-offs; **manual**; **hold** for payment not final, fraud review, a
  ship-not-before date (Shopify `SCHEDULED` + `fulfill_at`), address validation, or a merchant hold (Shopify `ON_HOLD`).
- **The shop sends intent; the warehouse decides capacity.** `FulfilmentOrder` carries `ShipNotBefore`, `ShipComplete`
  (true = no partial shipments), `BackorderPolicy` (`Allow` | `CancelRemainder`), `Priority` and service level, and the
  carrier and service chosen at checkout.
- **The warehouse answers Accepted or Rejected** (with a reason: unknown SKU, invalid address) in `FulfilmentProgress`,
  mirroring Shopify's accept/reject.

### 6.4 The stock feed

**Definitions in the wild** (§1.5): Shopify `available` = on_hand − committed − reserved − damaged − safety_stock −
quality_control; MSI salable = qty + reservations − threshold; Vendure saleable = onHand − allocated − threshold; ShipBob
suggests a buffer.

**The double-count problem.** The shop already counts its own sold-but-unshipped orders and its checkout holds (commerce
I1: `sold(q) + activeHolds(q) ≤ capacity(q)`, `docs/commerce_PRD.md:203`). If the warehouse sends "available = on-hand
− allocated", the shop subtracts those orders twice once the warehouse has allocated them. If it sends plain on-hand,
the shop's view is correct only when shipments and stock updates arrive in the same order; over a replication transport
the two are independent (§6.2). When "order shipped" arrives before "on-hand decreased", the shop believes it has one
unit too many: an **oversell window**.

**Recommended contract: cumulative counters in one document.** Per SKU and per channel, one warehouse-owned
`WarehouseStockLevel`:

```
OnHandSellable          // physical, in sellable status (excludes damaged, QC, quarantine, returns to inspect)
ReservedOtherChannels   // allocated/open quantity for orders from OTHER channels (B2B, a second shop)
ShippedTotal            // cumulative units shipped for THIS channel since the counter was created (monotonic)
Version, AsOfUtc        // monotonic; the consumer ignores anything older than what it has
```

The shop's capacity source (Checkout's capacity-source extension point, coupling rule `docs/commerce_PRD.md:184`)
sets:

```
capacity(q) = OnHandSellable − ReservedOtherChannels − Buffer(channel) + ShippedTotal
remaining   = capacity − sold − activeHolds
            = OnHandSellable − ReservedOtherChannels − Buffer − (sold − ShippedTotal) − activeHolds
```

`sold − ShippedTotal` is exactly "sold for this channel but not yet shipped, as of the warehouse snapshot". Both numbers
that move at ship-confirm (`OnHandSellable` down, `ShippedTotal` up) are in **one document, written in one transaction**,
so reordered or duplicated delivery of shipment documents can no longer cause oversell, and a stale
`WarehouseStockLevel` is simply superseded by `Version`.

| Event | Effect on the shop's capacity |
|---|---|
| Receipt into sellable | Rises |
| Ship | Neutral (on-hand −n, shipped +n) |
| Return restocked | +1 |
| Return scrapped | 0 |
| Shrinkage adjustment | Drops, possibly below `sold`. The shop stops selling; the warehouse short-picks and the backorder flow takes over |

**Proof sketch.** The domain sweep compared the shop's computed `remaining` with the true headroom over a stale snapshot:
shipments for this channel cancel out; shipments for other channels cancel out (on-hand and `ReservedOtherChannels` drop
together); receipts and restocks only make the shop pessimistic. So **zero oversell holds exactly for a SKU sold through
one channel**; with several channels the error is bounded by other-channel orders allocated during the replication lag.

The same argument in symbols (inference, derived while compiling this file). Take any snapshot the shop holds, written at
time *t*, with H = `OnHandSellable`, R = `ReservedOtherChannels` and S = `ShippedTotal`. Between *t* and the shop's
decision, let ΔS be units shipped for this channel, ΔO units shipped for other channels, ΔA the net other-channel
commitment not reflected in R (orders allocated since *t*, or sold but not yet received by the warehouse; allocations
consumed by shipment excluded), ΔI units received or restocked into sellable, and ΔL units lost. Ignoring the buffer:
- true headroom = (H − ΔS − ΔO + ΔI − ΔL) − (R − ΔO + ΔA) − (sold − S − ΔS) − holds
- computed `remaining` = H − R − (sold − S) − holds
- true − computed = ΔI − ΔL − ΔA

Shipments (ΔS, ΔO) cancel; receipts (ΔI ≥ 0) only make the shop pessimistic; other-channel commitments the snapshot does
not yet show (ΔA) are the only replication-induced error, and they are zero for a single-channel SKU. A loss discovered after the snapshot
(ΔL) is a physical event that no feed can anticipate; it surfaces as a short pick and a backorder (the event table above).
Because the result holds for **any** internally consistent past snapshot, delay, reordering and duplication do not matter,
provided the three numbers come from one document written in one transaction and the shop never moves back to an older
`Version`.

**The three rules the guarantee needs**
1. The shop decrements `sold` only when the warehouse **confirms** a cancellation or reports a short-ship cancellation,
   never on the request. Otherwise a rejected cancellation of a shipped unit counts that unit once in `ShippedTotal` and
   not at all in `sold`.
2. `sold` and `ShippedTotal` share an epoch. At onboarding, a baseline (units already shipped before the integration) is
   added to `ShippedTotal`.
3. Bundles map to component-SKU quotas through the commerce quota multipliers (the quota mapping carries a multiplier for
   bundles, `docs/commerce_PRD.md:216`).

**The levers with several channels:** a per-channel buffer, or **channel allotments**: escrow, splitting
`OnHandSellable` per channel (§2.7's bounded counters), which makes ΔA zero for the channel.

**Conflict with commerce I4.** I4 said that every return path, refunds included, returns each counter exactly once. For
warehouse-backed quotas, a refund **after shipment** must not return quota: if the item comes back and is restocked,
`OnHandSellable` already rises; if it is kept, lost or scrapped, nothing should become sellable. Only cancellations of
**unshipped** units return quota. Rule (research reading): for a quota whose capacity comes from a warehouse, `sold`
counts units sold minus units cancelled before shipment. The commerce PRD now records an I4 exception for shipped
physical goods (`docs/commerce_PRD.md:207-209`).

**Frequency, buffer, bundles**
- Push on change, debounced per SKU (about 1–5 s), plus a **periodic full reconciliation**: nightly, the shop compares
  its capacity inputs with a full warehouse report and alerts on drift. ShipHero and ShipBob both tell integrators to
  reconcile, because event delivery is not guaranteed; Shopify polls `fetch_stock` hourly as a backstop (§1.5).
- The **buffer** lives on the consumer side, per channel (a marketplace channel wants a bigger one), and is optional.
- **Checkout holds stay shop-only:** the warehouse never allocates for carts.
- **Bundles:** the shop maps a bundle to component quotas, as ShipBob tracks inventory at component level. The warehouse
  knows only physical SKUs.

**An acknowledgement formulation (Spark-fit sweep).** The shop sells against
`available = ATP − Σ(sold lines the warehouse has not yet acknowledged)`, where the warehouse states its acknowledgements
per order, or as a watermark, in the same contract as its available-to-promise. Without that, replication lag lets the
two apps oversell between them (spike WS5). (Inference: this is the same idea as `sold − ShippedTotal`, subtracting only
what the warehouse snapshot has not yet absorbed. The cumulative-counter version needs one number per SKU and channel;
the acknowledgement version needs per-order acknowledgements or a watermark in the same document.)

### 6.5 Shipments, partial shipments, backorders

- `WarehouseShipment` (warehouse-owned, immutable once `Shipped`): id `{fulfilmentOrderId}/shipments/{n}`; lines
  (fulfilment line id, quantity, lots and serials); packages (carton type, weight, dimensions, optional SSCC, tracking
  number, label reference); carrier and service; shipped-at.
- One order can produce several shipments (ShipBob split shipments; Shopify multiple fulfilments with line quantities).
- The remaining quantity follows the shop's `BackorderPolicy`: **backordered** stays open and is re-allocated when stock
  arrives; **cancelled** is reported in `FulfilmentProgress`, and the shop refunds and decrements `sold`.
- `ShipComplete=true` keeps the order unallocated until all lines can be allocated.

### 6.6 Cancellation handshake

Following Shopify's states:
1. The shop sets `CancellationRequested` (with a line subset) on its FulfilmentOrder.
2. The warehouse answers `CancellationAccepted` or `CancellationRejected(reason)` in `FulfilmentProgress`.

Warehouse rules, configurable (research reading):
- **Not yet picked:** accept; release allocations, void tasks.
- **Picked, not packed:** accept and create a put-back task, or reject (a switch).
- **Packed with a label:** accept only if the carrier label can be voided, else reject.
- **Shipped:** reject ("already picked up by the courier"); the shop turns it into a return.

ShipBob's simpler rule is "cannot cancel once picked" (§1.5). **Edits** after acceptance (address, lines) are modelled as
cancel + a new FulfilmentOrder; an address change uses the same handshake and is accepted only before the label exists.

### 6.7 Ids across shops

- **Natural ids everywhere:** the `FulfilmentOrder` id from the shop; warehouse `OutboundOrder` id =
  `outbound/{fulfilmentOrderId}`; shipments `{foId}/shipments/{n}`; movements keyed by the client command id.
  Re-delivery is then a no-op; ShipBob uses `reference_id` the same way.
- **Fulfilment-order ids must be unique across shops**, because one warehouse may serve several. RavenDB ETL preserves an
  id only when loading into the same collection name, where two shops' HiLo ids (`FulfilmentOrders/1-A`) collide; loading
  into another collection regenerates the id on every update (§3.5). The commerce PRD now requires ids unique across
  shops (`docs/commerce_PRD.md:319`).
- A monotonic `Version` on every replicated status document; consumers apply only newer versions, and status machines
  refuse illegal transitions (no `Picking` after `Cancelled`).

### 6.8 Returns

1. The shop creates the RMA (the 14-day withdrawal is in the commerce scope).
2. An `ExpectedReturn` replicates to the warehouse.
3. The warehouse receives it by scanning the RMA or order barcode, or blind.
4. It inspects and dispositions: **Restock** (to sellable), **Quarantine**, **Scrap** or **Return to vendor**.
5. `ReturnReceipt` replicates back. The shop refunds on receipt or after inspection (a shop setting).

Restock raises `OnHandSellable`; the quota consequences are in §6.4. EU withdrawal law allows the trader to withhold the
refund until the goods are received or proof of return is supplied (Directive 2011/83/EU art. 13(3)) [unverified]. The
commerce PRD now makes refunds for warehouse-backed stock wait for the warehouse's receipt, configurably
(`docs/commerce_PRD.md:320`).

### 6.9 Idempotency and ordering rules

1. Natural ids everywhere (§6.7).
2. A monotonic `Version` on every replicated status document (§6.7).
3. Cumulative counters, never deltas, across the boundary (§6.4).
4. Carrier webhooks (tracking) are at-least-once and unordered: dedupe on the provider message id and sort by event time
   (ShipBob, ShipHero). Commerce's `MintPlayer.Spark.Webhooks` (X5, `docs/commerce_PRD.md:482`) already plans signature
   checks, raw body and dedupe.
5. Reconcile daily (§6.4).

---

## 7. Proposed v1 scope and framework additions

This is the research proposal as of 2026-10-17. The PRD's decision log records what was decided; it departs from this
proposal in places.

### 7.1 In (v1)

| Area | In v1 | Why |
|---|---|---|
| Master data | Warehouse (site), Zone, Location: type Receiving / Storage / PickFace / Staging / PackStation / Quarantine / Returns, barcode + optional check digits, **walk sequence**, aisle/bay/level/position/side (for later heuristics), capacity hint, fixed item for pick faces. Item: SKU, GTINs, UoM sequence + pack sizes (each/case/pallet), weight/dimensions, tracking None/Lot/Serial, expiry on/off, minimum shelf life | Every system has these. WMSs sequence locations along a path rather than model geometry (§1.4), so a walk sequence gives route quality nearly for free |
| Stock | Balance per location × item × lot (+ expiry, + status), immutable movement ledger with reason codes, adjustments, moves, location-based cycle counts (frequency per location or class), pick-face min/max replenishment tasks, `WarehouseStockLevel` publication | The recurring core (§1.6) |
| Inbound | Expected receipt (ASN) or blind receipt, scanning with GS1 parsing (lot/expiry), putaway rules (fixed location per item → zone per category → nearest empty), putaway tasks | Keeps location stock true |
| Outbound | FulfilmentOrder intake, release policies, accept/reject, allocation rules (FIFO/FEFO/LIFO/closest), pick strategies **discrete, batch, cluster-to-tote** (zone and wave as rules on top), routing by walk sequence (+ optional S-shape), scan-to-confirm, short-pick handling, backorders, cancellation handshake | The picking product itself |
| Packing | Pack station: scan tote/order → verify lines by scan → choose carton (manual or volumetric) → weigh → carrier label through the commerce carrier adapters → packing slip PDF → ship-confirm. Optional SSCC per carton; end-of-day handover/manifest per carrier | Closes the loop to tracking |
| Returns | Expected return + receipt + disposition | Required by the shop's withdrawal flow |
| Metrics | Event log (task started/completed, line confirmed, short pick, scan mismatch, pack/ship) → lines/hour and units/hour per user, pick exception rate, release→ship cycle time, dock-to-stock time, count accuracy; on the Dashboards plugin | Cheap if the events exist from day one |
| Devices | Keyboard-wedge + camera scan layer, task-directed mobile pages, pack-station page | §2 |
| Printing | Print job queue + PDF and Agent adapters first; QZ Tray, Browser Print and RawTcp as adapters | §2.6 |

### 7.2 Out (v1), with reasons

- **Labour-management standards and incentive pay:** enterprise-grade, and they need engineered standards; v1 only
  records the events that would feed them.
- **Slotting optimisation** (velocity-based re-slotting): needs months of history and is a planning tool; v1 has fixed
  pick faces and min/max.
- **Yard and dock scheduling, cross-docking, kitting/VAS, light manufacturing:** separate domains. Bundles are handled
  shop-side.
- **Automation** (WCS/WES, conveyors, AMRs, pick-to-light, voice): hardware-specific. The extension point is the event
  stream and the task API.
- **Purchasing, supplier management, inventory valuation and costing:** the shop or an ERP owns these. FIFO *allocation*
  is not FIFO *costing*.
- **3PL multi-client billing.** Whether to reserve an `Owner` dimension in the balance key now was an open question (cheap
  now, expensive later).
- **Inter-warehouse transfer orders and multi-site stock balancing.** Intra-site moves are in.
- **Full 3D cartonisation as the default:** an optional adapter only (for example
  [3DContainerPacking](https://github.com/davidmchapman/3DContainerPacking), MIT, EB-AFIT).
- **Offline receiving, counting and adjusting** (§2.7, rule 4).
- **RFID, and serial capture at receiving.** Serial capture at pack is optional.
- **A general route/rule engine** (Odoo push/pull): a fixed pipeline whose steps can be skipped is enough.

### 7.3 Package decomposition ("many small libraries with a concise purpose")

The decomposition follows the repository's plugin conventions (`spark.AddX(options)` with a `Spark:X` section,
`<SparkLibraryAlias>`, library `security.json` slots, Angular secondary entry points of `@mintplayer/ng-spark`;
`docs/commerce_research.md` §2). **Entity names must be unique across loaded assemblies**: Actions resolve by simple name
and duplicates throw (`S/Services/ActionsResolver.cs:120-163`). Hence `WarehouseLocation`, `StockBalance`,
`StockMovement`, `PickTask`, `PickBatch`, `OutboundOrder`, `InboundReceipt`, `CycleCount`, `WarehouseShipment`, and never
`Location`, `Order` or `Shipment`.

| Package (alias) | Purpose | Depends on |
|---|---|---|
| `MintPlayer.Spark.Fulfilment.Contracts` (new, tiny) | The wire contract both apps reference: FulfilmentOrder, FulfilmentProgress, WarehouseShipment, WarehouseStockLevel, ExpectedReturn, ReturnReceipt. **Split out of `Commerce.Fulfilment`** so the warehouse never references Checkout (coupling rule, `docs/commerce_PRD.md:184`) | Replication.Abstractions |
| `MintPlayer.Spark.Gs1` | GTIN/SSCC check digits, SSCC allocation from a sequence, AI element-string and Digital Link parsing (dictionary as data). Generic: commerce can validate GTINs with it | — |
| `MintPlayer.Spark.Warehouse.Abstractions` | Strategy interfaces: `IAllocationRule`, `IPickStrategy`, `IPickRouter`, `IPutawayRule`, `ICartonizer`, `ILabelTemplate`; value types (Quantity + UoM, LocationCode) | — |
| `MintPlayer.Spark.Warehouse` (`warehouse`) | Master data (sites, zones, locations, items, UoM) + the **stock engine**: balances, ledger, adjustments, moves, invariants, stock-level publication | Abstractions, Gs1, Messaging |
| `MintPlayer.Spark.Warehouse.Inbound` | Expected receipts, receiving, putaway rules and tasks, returns receipt and disposition | Warehouse |
| `MintPlayer.Spark.Warehouse.Outbound` | Intake from contracts, release, allocation, pick batching/cluster, routing, pick tasks, short picks, backorders, cancellation handshake | Warehouse, Fulfilment.Contracts |
| `MintPlayer.Spark.Warehouse.Packing` | Pack station, cartonisation (none/manual/volumetric built in), shipment build, SSCC, handover/manifest; carrier labels through the commerce carrier-adapter **abstraction** | Outbound, `Commerce.Shipping` abstractions only |
| `MintPlayer.Spark.Warehouse.Counting` | Cycle-count scheduling, blind counts, variance approval → adjustments; min/max replenishment tasks (or folded into the core) | Warehouse, Cron |
| `MintPlayer.Spark.Printing` (+ `.Zpl`, `.Agent`, `.RawTcp`, `.QzTray` signing endpoint) | Generic print job queue + adapters. **Not warehouse-specific**: tickets and invoices can use it | Messaging |
| `MintPlayer.Spark.Printing.AgentHost` | The on-premises worker (dotnet tool / Windows service) that pulls jobs and prints to 9100 | — |
| `MintPlayer.Spark.Warehouse.Cartonization.ThreeD` (optional) | 3D packing adapter | Abstractions |
| `@mintplayer/ng-spark/scanning` | Generic scan layer: wedge capture, camera through `BarcodeDetector` + self-hosted `barcode-detector` WASM, GS1 parsing; **shared with the ticket scanner PWA** | — |
| `@mintplayer/ng-spark/offline` | IndexedDB command outbox with idempotency keys, replay, exception list; builds on `ng-spark/pwa`; **shared with the ticket scanner** | pwa |
| `@mintplayer/ng-spark/printing` | Client adapters: PDF print, QZ Tray, Zebra Browser Print | — |
| `@mintplayer/ng-spark/warehouse` (+ `/picking`, `/packing`, `/receiving`, `/counting`) | Task-directed mobile pages and the pack-station page; replaceable per route like the commerce pages | scanning, offline, printing |

**Shop side** (commerce work, not warehouse work): `Commerce.Fulfilment` references `Fulfilment.Contracts`, implements
the capacity source (§6.4) and reacts to the replicated `FulfilmentProgress`, `WarehouseShipment` and `ReturnReceipt`.

**Existing blocks to compose:** Messaging (allocator queue and outbox); Replication; Cron (count scheduling, nightly
reconciliation, replenishment sweeps); Notifications (low stock, exceptions, short picks); Dashboards (KPIs); Webhooks
(X5) for carrier tracking; the planned claims primitive (X12) for "a picker claims a batch, with takeover after lease
expiry"; the quotas OCC retry pattern (`libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77`).

### 7.4 Configuration switches (`Spark:Warehouse:*`, overridable per site or zone)

| Switch | Values (default first) | Notes |
|---|---|---|
| `Release:Mode` | `Immediate` \| `Scheduled(times)` \| `Manual` | Holds come from the shop (`ShipNotBefore`, hold flags) |
| `Allocation:Rule` | `Fifo` \| `Fefo` \| `Lifo` \| `ClosestLocation` \| custom `IAllocationRule` | FEFO uses expiry minus `MinShelfLifeDays` (Odoo's removal-date idea) |
| `Allocation:When` | `OnRelease` \| `Manual` \| `BeforeShipDate(days)` | Mirrors Odoo's "Reservation Method" |
| `Picking:Strategy` | `Discrete` \| `Batch(maxOrders, maxLines)` \| `Cluster(totesPerCart)` \| `Zone` | Wave = scheduled release + a grouping rule (`Carrier`, `CutOff`, `Zone`, `Category`) |
| `Picking:Routing` | `WalkSequence` \| `SShape` \| `LargestGap` \| `None` | Walk sequence = location sort key; the heuristics need aisle geometry |
| `Picking:Confirm` | `ScanLocationAndItem` \| `ScanItem` \| `Manual` | + `LocationCheckDigits`, `QuantityEntry: PerScan` \| `Prompt` |
| `ShortPick:Policy` | `ReallocateThenBackorder` \| `Backorder` \| `CancelRemainder` \| `HoldOrder` | + `TriggerCycleCount: true` |
| `Cancellation:AfterPick` | `AcceptWithPutBack` \| `Reject` | §6.6 |
| `Packing:Verify` | `ScanEveryUnit` \| `ScanEveryLine` \| `None` | |
| `Packing:Cartonization` | `Manual` \| `Volumetric` \| `ThreeD` | |
| `Packing:Sscc` | `Off` \| `On(companyPrefix, extensionDigit)` | |
| `Printing:Adapter` (per printer) | `Pdf` \| `Agent` \| `QzTray` \| `ZebraBrowserPrint` \| `RawTcp` | + `LabelFormat: ZPL203` \| `ZPL300` \| `PdfA6` |
| `Carrier` | reuse `spark.AddShipping().UseSendcloud()` etc. | The host picks vendors (commerce K7) |
| `Offline:Mode` | `Off` \| `AssignedTasksOnly` | §2.7 |
| `Counting` | `Blind: true`; `Schedule: PerLocation(days)` \| `Abc(30/60/120)`; `Tolerance` → review | Blind counts as in D365; the GP ABC example (§1.1) |
| `Replenishment` | `MinMax(daily + on short pick)` \| `Off` | Fixed pick faces only (D365 cannot replenish an unassigned empty location) |
| `Scanning:Sources` | `Wedge,Camera` | + `CameraFallback: Polyfill` \| `None`; `WasmUrl` self-hosted |
| `Tracking` (per item) | `None` \| `Lot` \| `Serial`; `Expiry: on/off` | |
| `StockFeed` | `Debounce: 2s`; `Reconcile: daily`; `Channels` | The buffer stays shop-side |

### 7.5 Framework additions WX1–WX12

| # | Addition | Why | Evidence of the gap | Shared with commerce? |
|---|---|---|---|---|
| WX1 | **Replication transport abstraction plus an HTTP transport**: producer contracts, durable capture, versioned idempotent apply, digest/fetch reconciliation, `IReplicaChanged<T>`; ETL stays as an optional transport | Community has no ETL, and production is Community | §3.2; `docs/endpoints_generator_adoption_plan.md:219` | Yes: the fulfilment contract (commerce §4.9, S20) |
| WX2 | **Replication declarations bound from configuration**: source module and contract from configuration; N sources per contract with id namespacing; write-back routed per document | Generic libraries cannot hard-code `SourceModule`; 3PL | `RA/ReplicatedAttribute.cs:9-35` | Yes (commerce consumes warehouse stock) |
| WX3 | **Hardening, if SyncAction is kept**: durable in-commit dispatch, 207 treated per action, configurable budget plus re-driving Failed actions, per-document ordering, idempotency keys, writable fields scoped per module | WF1–WF4, WF6, WF8 | §4 | Yes |
| WX4 | **Replication security**: no consumer JavaScript (owner projections), ETL task removal, an operator-managed read-only registry mode, a startup licence check | WF5, WF7, WF10; the registry trust root | §3.4 | Yes |
| WX5 | **Scanning primitives** (`ng-spark/scanning`): wedge, camera, GS1 parser, scan-driven step flow | No scanning code anywhere | §3.8 | Yes: the scanner PWA (commerce §4.11) |
| WX6 | **Document and label rendering**: 1D/2D barcodes, a PDF seam, label templates, print delivery adapters | QR only, inside Authorization; no PDF in `libs/` | `Auth/Identity/SparkQrCodeRenderer.cs:17-44`; `apps/SeoTester/SeoTester/Reports/ReportPdf.cs:124` | Yes: invoices, tickets (commerce §4.8, §4.11) |
| WX7 | **Offline command queue** (IndexedDB + idempotent replay + conflict report) | The PWA caches no data | `docs/guide-pwa.md:33-34`; `tools/verify-ngsw-config.test.mjs:90-96` | Yes: offline ticket scanning (S9) |
| WX8 | **`Quantity` / unit-of-measure value type**, generalising X4 | No unit-bearing quantity | `A/Model/SparkModelShape.cs:191-216` | Builds on X4 |
| WX9 | **Hierarchical reference picker** plus materialized-path helpers | The reference picker is flat | `NS/po-form/src/reference-query-tree-select-provider.ts:35-38` | Venues and blocks could use it |
| WX10 | **Device enrollment and quick user switch** (badge + PIN, scoped device token) | Shared handhelds | §3.9 | Yes: the scanner device token |
| WX11 | **Generic CSV import/export** (locations, counts, items) | None in `libs/` (grep) | — | Yes: the seat CSV (commerce D32) |
| WX12 | **Carrier adapters split from Checkout** (a package-map change) | The warehouse prints labels at packing | `docs/commerce_PRD.md:314`; since applied in the commerce package map, `:166` | Yes |
| (deps) | Commerce X2 (private stream), X3 (atomic natural-id create), X8 (custom roles), X12 (claims) | Leases, task push, the stock-record create race, roles | `docs/commerce_PRD.md:479-489` | They are commerce's |

### 7.6 Impacts on the commerce PRD (as proposed by the research)

- **C1.** Settle the transport with WX1 (the HTTP transport); run S20 over HTTP on Community, and optionally over ETL on
  Developer. At research time the package map described `Commerce.Fulfilment` as a "contract for `[Replicated]` ETL +
  SyncAction"; it now calls it the shop side of the fulfilment contract, with the contract itself in a separate package
  (`docs/commerce_PRD.md:168`).
- **C2.** Fulfilment-order ids unique across modules (multi-shop warehouse; same-collection id preservation), and
  **cancel, never hard-delete** (ETL deletes propagate by default; in the HTTP design a tombstone deletes the replica).
  Now in the commerce PRD (`docs/commerce_PRD.md:318-319`).
- **C3.** Shop variants need `Sku`, `Gtin`s, weight and dimensions for the warehouse's item replica.
- **C4.** The capacity-source extension point needs the acknowledgement handshake or the cumulative counters (§6.4).
- **C5.** WX12, the carriers split (now in the package map, `docs/commerce_PRD.md:166`). WX5, WX6 and WX7 move out of
  `Ticketing.Tickets` and invoicing into shared packages (the coupling rule, `:184`).
- **C6.** Returns and the 14-day withdrawal probably refund only after a warehouse `return-receipts` signal (now
  configurable in the commerce PRD, `docs/commerce_PRD.md:320`).
- **C7.** WF1–WF10 are in code that ships today, so they belong in this PR (the one-PR rule); tracked as F12–F18 (§4).

### 7.7 Questions the research put to the owner

The grilling answered these; the PRD's decision log holds the answers.
- From the domain sweep: the I4 refinement for warehouse-backed quotas (§6.4); reserving an `Owner` (3PL client)
  dimension in balances now; one warehouse app serving several shops or channels in v1, or one shop
  (`ReservedOtherChannels` assumes several are possible); item-master ownership (replicated from the shop plus
  warehouse-owned extras, or warehouse-owned with an optional feed); the printing default (PDF-only plus Agent, or QZ Tray
  in v1); offline in v1 at all, or only after the ticket scanner's outbox (S9); whether an SSCC/GS1 company prefix is
  realistic for target users; the default for cancellation after pick (`AcceptWithPutBack` or `Reject`).
- From the Spark-fit sweep: WQ1 transport (HTTP push plus reconciliation, or require an ETL-capable licence); WQ2 the
  direction of writes (state replication both ways, or SyncAction commands into shop documents); WQ3 scope (one stock
  owner or 3PL; one warehouse or several); WQ4 tracking (lots, expiry, serials); WQ5 picking modes and processes; WQ6
  offline; WQ7 hardware (wedge or cameras, ZPL or PDF, scales); WQ8 carriers (who calls the carrier, who receives tracking
  webhooks); WQ9 item master; WQ10 device sign-in (shared handhelds with badge and PIN, or personal accounts); WQ11
  deployment of the demo; WQ12 whether the warehouse gates refunds on goods received.

---

## 8. Spikes proposed

### 8.1 Domain spikes W1–W9

| # | Risk | Spike | Pass criterion |
|---|---|---|---|
| W1 | Stock invariants under concurrency (on-hand ≥ 0, allocated ≤ on-hand, ledger = balances) with concurrent pick confirms, adjustments and counts on hot pick faces | Randomised concurrent workload on RavenDB, single node and a 3-node cluster (cluster-wide transactions), with duplicated and retried commands | **0** invariant violations over ≥ 100 runs × 10k operations. Σ ledger = balances exactly. Duplicate idempotency keys applied once. p95 pick-confirm ≤ 150 ms (single node). Retry rate on the hottest balance reported |
| W2 | Stock-feed oversell across an eventually consistent transport (§6.4) | A simulator with shop sales, holds, cancellations, warehouse shipments, returns and adjustments under random delay, reordering and duplication; then the same over a real two-app transport | Single channel: **0** oversell in ≥ 1,000 randomised runs. Multi-channel: oversell ≤ other-channel units allocated within the measured lag, and 0 with allotments. Real transport: a warehouse change visible in shop capacity ≤ 10 s p95. Proof that the I4 refinement and the confirmed-cancel rule are needed (failing runs without them) |
| W3 | Transport behaviour assumptions (cross-collection order, task redeploy, deletes); written for ETL | Two Spark apps: 10k FulfilmentOrders, task reset/redeploy, cancellation by status | 10k orders → exactly 10k `OutboundOrder` (natural ids), 0 duplicates after reset. Cancellation round trip ≤ 5 s p95. Cross-collection ordering observed and documented |
| W4 | Keyboard-wedge decoding on real devices (Zebra and Honeywell with Chrome, Windows AZERTY with a USB HID scanner), GS1 with FNC1/GS | A test sheet of Code 128 locations, EAN-13, GS1-128 (01)(17)(10) and GS1 DataMatrix with GS; 1,000 scans per device | **100%** correct element strings per device. No soft keyboard popping up. Scan → UI ≤ 150 ms p95. Only a documented scanner/DataWedge profile needed, no per-device code |
| W5 | Camera scanning throughput and coverage | `BarcodeDetector` (Chrome Android) against the `barcode-detector` WASM on a ≤ €200 Android, an iPhone (Safari) and a Windows laptop, under warehouse lighting | ≥ 95% decoded within 1.5 s at 15–30 cm for EAN-13 and Code 128. Works offline with the self-hosted WASM. Bundle impact measured |
| W6 | Label printing from a cloud-hosted server | The same job through Agent, QZ Tray (signed), Browser Print and PDF; Sendcloud `application/zpl` 203 dpi test labels | Pack-complete → label out ≤ 3 s p95 (agent or bridge). The printed barcode scans back correctly. Printer offline → queued, retried, **exactly one** label (no duplicates). No dialog for agent or bridge |
| W7 | Offline assigned-task picking (only if enabled) | A picker completes a 30-line batch in airplane mode, plus a conflicting supervisor move | All confirmations applied exactly once on reconnect. Conflicts land in the exception queue. 0 negative balances. The outbox survives an app restart. `persist()` granted to the installed PWA on Zebra Chrome |
| W8 | Release, allocation and batching performance | 1,000 orders × 3 lines, 5,000 locations, FEFO | Release + allocate + batch ≤ 30 s (single node). Route sequencing of a 50-line batch ≤ 50 ms |
| W9 | Genericity of the contract (framework, not platform) | A stub non-Spark-commerce producer drives the warehouse through `Fulfilment.Contracts` only | The warehouse host builds without Commerce Checkout. The stub shop completes order → ship → return. Library diagnostics clean |

### 8.2 Spark-fit spikes WS1–WS9

| # | Question | Pass criterion |
|---|---|---|
| WS1 | Does the HTTP transport survive failures? (consumer down 2 h, producer crash after commit, duplicates, reordering, a poison payload) | Converges within one reconciliation period; 0 lost and 0 duplicated state changes; no new subscriptions; Community licence |
| WS2 | What are ETL's real semantics? (Developer licence) | Id preservation in the same collection; whether target-only fields survive; delete behaviour and `deleteDocumentsBehavior`; multi-source collision; ETL keeps flowing while the web app is down; startup refusal on Community |
| WS3 | Red tests for WF1–WF8 | Each defect red, then green |
| WS4 | Stock-move concurrency: 50 movers on 10 hot stock records | 0 negative stock, 0 lost updates, ledger sum = balances; throughput recorded |
| WS5 | Cross-app no-oversell (acknowledgement handshake) | 0 oversell over ≥ 1,000 randomised runs with replication lag injected |
| WS6 | Scanning on real devices | Zebra/Honeywell wedge, Android `BarcodeDetector`, iOS fallback; a GS1 test corpus parses 100% |
| WS7 | Label printing | A 4×6 PDF prints through a thermal printer driver; ZPL passthrough works; Code 128 and GS1-128 scan back |
| WS8 | Live pick queue with 50 handhelds | Re-evaluation cost recorded; behaviour of shared accounts against the socket cap |
| WS9 | Offline replay | 30 minutes offline, 200 queued confirmations: idempotent replay, conflicts reported |

### 8.3 Overlaps (inference)

The two sweeps proposed their spikes independently, and several measure the same thing: W1 ≈ WS4 (stock concurrency),
W2 ≈ WS5 (cross-app oversell), W3 ≈ WS2 (transport semantics), W4 + W5 ≈ WS6 (scanning), W6 ≈ WS7 (printing),
W7 ≈ WS9 (offline replay). WS1 (transport failures), WS3 (red tests for the defects), WS8 (live queue cost), W8
(allocation performance) and W9 (contract genericity) have no counterpart. Where a pair differs, the stricter criterion
is the W one in W1, W2, W4–W7 (it states latency and exactly-once bounds).

---

## 9. Sources

**Repository documents cited.** `docs/guide-cross-module-sync.md` · `docs/guide-replication-mtls.md` ·
`docs/guide-authorization.md` · `docs/guide-auditing.md` · `docs/guide-concurrency.md` · `docs/guide-pwa.md` ·
`docs/guide-live-updates.md` · `docs/guide-expiration.md` · `docs/guide-asdetail-attributes.md` ·
`docs/endpoints_generator_adoption_plan.md` · `docs/multi-host-e2e-plan.md` · `docs/coverage-handoff-plan.md` ·
`docs/issue_460_PRD.md` · `docs/identity_provider_platform_PRD.md` · `docs/contributions_PRD.md` ·
`docs/findings-replication-mtls.md` · `docs/messaging_single_subscription_PRD.md` · `docs/actions_and_coverage_PRD.md` ·
`docs/code-coverage/raven-tls-migration.md` · `docs/coverage_commit_graph_PRD.md` · `docs/commerce_PRD.md` ·
`docs/commerce_research.md` · `libs/history/MintPlayer.Spark.History/README.md` · the messaging README · `CLAUDE.md`.

**RavenDB.** Raven ETL (id rules, update and delete behaviour, `load(id)`, behaviour functions):
https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/raven · https://docs.ravendb.net/7.1/server/ongoing-tasks/etl/raven ·
ETL basics (fallback mode, batching, transactional apply): https://docs.ravendb.net/7.0/server/ongoing-tasks/etl/basics ·
licensing overview: https://docs.ravendb.net/7.1/start/licensing/licensing-overview · pricing (Community: 3 subscriptions per
database, 15 per cluster, 6 GB, cluster of 3; ETL not listed): https://ravendb.net/buy · 4.0 licensing matrix (Community ETL
"No"): https://ayende.com/blog/178434/ravendb-4-0-licensing-pricing · cluster transactions:
https://docs.ravendb.net/7.1/client-api/session/cluster-transaction/overview

**WMS domain: academic.** de Koster, Le-Duc & Roodbergen 2007 (EJOR PDF):
https://pure.eur.nl/ws/portalfiles/portal/46713708/DesignandControl_2007.pdf · working-paper version:
https://repub.eur.nl/pub/7322 · Bartholdi & Hackman v0.98.1: https://www.warehouse-science.com/book/editions/wh-sci-0.98.1.pdf ·
Roodbergen background: https://www.roodbergen.com/warehouse/background.php · Interactive Warehouse:
https://www.roodbergen.com/warehouse/index.php · Ratliff & Rosenthal 1983: https://ideas.repec.org/a/inm/oropre/v31y1983i3p507-521.html ·
Petersen 1997: https://www.doi.org/10.1108/01443579710177860 · Pansart et al.: https://arxiv.org/pdf/1703.00699

**Dynamics 365 SCM.** https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/tasks/configure-locations-wms-enabled-warehouse ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/batch-and-license-plate-confirmation ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/unit-measure-stocking-policies ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/cycle-counting ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/reason-codes-for-counting-journals ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/replenishment ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/piece-picking-confirmation ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/tasks/set-up-short-picking-item-reallocation ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/put-to-wall-put-to-store ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/packing-containers ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/wave-containerization ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/warehouse-slotting ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/mhax ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/sales-returns ·
https://learn.microsoft.com/en-us/dynamics365/supply-chain/sales-marketing/disposition-and-return-reason-codes ·
https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/analytics/warehouse-power-bi-content ·
Warehouse Management mobile app: https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/install-configure-warehouse-management-app

**Business Central and GP.** https://learn.microsoft.com/en-us/dynamics365/business-central/design-details-warehouse-management ·
https://learn.microsoft.com/en-us/dynamics365/business-central/inventory-how-count-adjust-reclassify ·
GP ABC example: https://github.com/MicrosoftDocs/msftdynamicsgpdocs/blob/85592665fc2e196fa78827ae8df215e306e29cc0/dynamics-gp/distribution/Inventory2.md

**Odoo.** https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/operation_type.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/removal_strategies.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/removal_strategies/fefo.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/putaway.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/receipts_three_steps.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/batch.html ·
https://www.odoo.com/documentation/19.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/wave.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/picking_methods/cluster.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/cycle_counts.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/replenishment/reordering_rules.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/barcode/operations/gs1_nomenclature.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/setup_configuration/dispatch.html ·
https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/shipping_receiving/daily_operations/owned_stock.html ·
three-step delivery (13.0): https://www.odoo.com/documentation/user/13.0/uk/inventory/management/delivery/three_steps.html ·
`stock_barcode` absent from Community (404): https://github.com/odoo/odoo/tree/18.0/addons/stock_barcode

**Other WMS and ERP systems.** ERPNext: https://raw.githubusercontent.com/frappe/erpnext/develop/erpnext/stock/doctype/bin/bin.json ·
https://docs.frappe.io/erpnext/projected-quantity · https://docs.frappe.io/erpnext/pick-list ·
https://docs.frappe.io/erpnext/stock-reservation · https://docs.frappe.io/erpnext/putaway-rule · OFBiz:
https://raw.githubusercontent.com/apache/ofbiz-framework/trunk/applications/datamodel/entitydef/product-entitymodel.xml ·
https://raw.githubusercontent.com/apache/ofbiz-framework/trunk/applications/datamodel/entitydef/shipment-entitymodel.xml ·
OpenBoxes: https://docs.openboxes.com/en/latest/ · https://docs.openboxes.com/en/latest/about/features/ ·
https://github.com/openboxes/openboxes · https://help.openboxes.com/article/289-managing-bin-and-zone-locations · Dolibarr:
https://raw.githubusercontent.com/Dolibarr/dolibarr/develop/htdocs/langs/en_US/stocks.lang

**Commerce, 3PL and SaaS.** Shopify:
https://shopify.dev/docs/apps/build/orders-fulfillment/inventory-management-apps/manage-quantities-states ·
https://shopify.dev/docs/apps/build/orders-fulfillment/fulfillment-service-apps ·
https://shopify.dev/docs/apps/build/orders-fulfillment/fulfillment-service-apps/build-for-fulfillment-services ·
https://shopify.dev/docs/api/admin-graphql/latest/enums/FulfillmentOrderStatus ·
https://shopify.dev/docs/api/admin-graphql/latest/enums/FulfillmentOrderRequestStatus · ShipBob:
https://developer.shipbob.com/2026-07/guides/build-orders-integration.md · https://developer.shipbob.com/2026-07/webhooks.md ·
https://developer.shipbob.com/2026-07/guides/inventory.md · ShipHero: https://developer.shiphero.com/webhooks/ · ShipStation:
https://docs.shipstation.com/apis/shipengine/docs/labels/formats.md · https://docs.shipstation.com/managing-inventory.md ·
Sortly: https://www.sortly.com/features/ · Magento MSI:
https://github.com/magento/inventory/wiki/Salable-Quantity-Calculation-and-Mechanism-of-Reservations · Vendure:
https://docs.vendure.io/guides/core-concepts/stock-control/ · Saleor: https://docs.saleor.io/developer/stock/overview ·
https://docs.saleor.io/developer/stock/stock-allocation · https://docs.saleor.io/developer/stock/stock-reservation · Medusa:
https://docs.medusajs.com/resources/commerce-modules/inventory/concepts

**EDI (Stedi).** https://www.stedi.com/edi/x12/transaction-set/940 · https://www.stedi.com/edi/x12/transaction-set/945 ·
https://www.stedi.com/edi/x12/transaction-set/943 · https://www.stedi.com/edi/x12/transaction-set/944 ·
https://www.stedi.com/edi/x12/transaction-set/846 · https://www.stedi.com/edi/x12/transaction-set/947 ·
https://www.stedi.com/edi/x12/transaction-set/180

**Standards, benchmarks, market.** GS1 Logistic Label Guideline 1.3:
https://www.gs1.org/standards/gs1-logistic-label-guideline/current-standard · WERC DC Measures:
https://werc.org/page/ASSESS-DC_Measures · WERC metric list: https://werc.org/page/ASSESS-Benchmarking_Overview · Gartner WMS
market: https://www.gartner.com/reviews/market/warehouse-management-systems

**Cartonisation.** https://github.com/davidmchapman/3DContainerPacking · https://github.com/dvdoug/BoxPacker ·
https://github.com/StephenPAdams/SixFourThree.BoxPacker · https://github.com/skjolber/3d-bin-container-packing

**Hardware and browser.** Zebra: https://techdocs.zebra.com/datawedge/latest/guide/output/keystroke/ ·
https://techdocs.zebra.com/datawedge/latest/guide/output/intent/ · https://techdocs.zebra.com/datawedge/latest/guide/profiles/ ·
Enterprise Browser (archived landing): https://techdocs.zebra.com/enterprise-browser/ · Browser Print:
https://www.zebra.com/us/en/support-downloads/software/printer-software/browser-print.html · Link-OS:
https://techdocs.zebra.com/link-os/ · MDN: https://developer.mozilla.org/en-US/docs/Web/API/Barcode_Detection_API ·
https://raw.githubusercontent.com/mdn/browser-compat-data/main/api/BarcodeDetector.json ·
https://raw.githubusercontent.com/mdn/browser-compat-data/main/api/SyncManager.json ·
https://developer.mozilla.org/en-US/docs/Web/API/Background_Synchronization_API ·
https://developer.mozilla.org/en-US/docs/Web/API/Storage_API/Storage_quotas_and_eviction_criteria ·
https://developer.mozilla.org/en-US/docs/Web/API/WebUSB_API · https://developer.mozilla.org/en-US/docs/Web/API/KeyboardEvent/code ·
Chrome: https://developer.chrome.com/blog/background-sync · https://developer.chrome.com/docs/capabilities/usb · Scanning
libraries: https://github.com/Sec-ant/barcode-detector · https://github.com/zxing-js/library · https://github.com/micjahn/ZXing.Net

**GS1.** AI dataset: https://ref.gs1.org/ai/GS1_Application_Identifiers.jsonld · Barcode Syntax Engine:
https://github.com/gs1/gs1-syntax-engine · GS1 US Sunrise 2027: https://www.gs1us.org/industries-and-insights/by-topic/sunrise-2027 ·
secondary: https://en.wikipedia.org/wiki/Serial_shipping_container_code · https://en.wikipedia.org/wiki/GS1-128

**Printing and carriers.** QZ Tray: https://qz.io/docs/ · https://raw.githubusercontent.com/qzind/tray/master/LICENSE.txt ·
BinaryKits.Zpl: https://github.com/BinaryKits/BinaryKits.Zpl · Labelary: https://labelary.com/service.html · JetDirect / 9100
(secondary): https://en.wikipedia.org/wiki/JetDirect · Sendcloud:
https://sendcloud.dev/api/v3/parcel-documents/retrieve-a-parcel-document.md · https://sendcloud.dev/docs/shipments/print-your-labels.md ·
https://sendcloud.dev/llms.txt · PostNL Shipment V4: https://developer.postnl.nl/integration-with-postnl/api-overview/shipment-v4/ ·
dimensional weight (secondary): https://en.wikipedia.org/wiki/Dimensional_weight

**Offline theory.** Balegas et al. 2015: https://arxiv.org/abs/1503.09052

**Caveats on sources.** The web-search budget ran out early, so coverage leans on pages that could be fetched directly;
gs1.org returned 403 and the Zoho Inventory pages 403/404. The textbook cost shares (55%, 60%, travel 50%) and the returns
figures are old estimates repeated across the literature. SSCC, GS1-128, JetDirect and dimensional-weight details rest on
Wikipedia. The WERC benchmark values are paywalled. Vendor pages (Zebra, QZ, Sendcloud, ShipBob, ShipHero, Shopify) were
used for mechanisms, not effectiveness. Repository line numbers hold for master `e39b16de`; `docs/commerce_PRD.md` line
numbers drift because that document was being edited on this date.
