# Carrier fixtures (spike S13, fixture-based: round 4 and round 5, D37)

No test calls a carrier. These files stand in for the carriers' answers. Each states where it comes from (`_fixture` in
JSON, a comment in XML) and how it was changed. None is a recording of our own account: the owner creates the
development accounts later, and recordings then replace these.

| Folder | Source | Status |
|---|---|---|
| `Sendcloud/` | Sendcloud's API v3 reference pages (sendcloud.dev: shipments announce/retrieve/cancel, returns, parcel documents, tracking, service points) and the parcel-status webhook page | **doc-derived**: documented examples, trimmed; the second parcel, the return object, the tracking events beyond the first and the locker/expired points are built from the schema |
| `PostNL/` | PostNL's published OpenAPI files (`api.postnl.nl/shipment/*/swagger.json`) | **doc-derived**: documented examples; the locations answer is built from the schema (its example is a placeholder) |
| `Bpost/` | bpost publishes no API reference. Orders and labels come from the shipping-manager library bpost's plugins use (`Antidot-be/bpost-api-library`, its `tests/BpostApiExamples`) | **SDK-derived**, not official |
| `Bpost/locator.xml` | one answer of the public pick-up point locator (`pudo.bpost.be/Locator`), called with a dummy partner id | recorded from a public endpoint, no account |

What the sources could not confirm is listed in each adapter's README.
