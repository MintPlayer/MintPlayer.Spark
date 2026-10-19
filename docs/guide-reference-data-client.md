# Reference data: the consumer side

Background: `docs/reference_data_PRD.md` §4.7, §4.13–§4.15. The instance is `docs/guide-reference-data.md`.

## .NET

```csharp
spark.AddReferenceDataClient();   // typed /api/v1 client, Address behaviour, zone names, house-number hook, release sync
```

```jsonc
"Spark": {
  "ReferenceData": {
    "Client": {
      "BaseAddress": "https://refdata.example.org/",   // no default; unset = free-text mode (RD-DEF 8)
      "PublicKey": "…",                                // optional: the instance's Ed25519 public key (base64); then releases must be signed
      "Sync": { "Enabled": true, "Schedule": "0 15 4 * * *", "Countries": [ "be" ] },
      "HouseNumbers": { "Enabled": false, "Timeout": "00:00:02" }
    }
  }
}
```

- **`Address`** (`MintPlayer.Spark.ReferenceData`, in the plain-SDK Abstractions package, so an entity library can
  declare it): a `[ValueObject]` with the country, the street, postal code, locality and municipality ids beside their
  names, `TimeZone`, the rendered `Lines` and `Verified`/`HouseNumberVerified`. It is **flat** (one level), because the
  generic form edits a single AsDetail object as one record (X15).
- **The behaviour** runs before every save of an entity holding an `Address` (directly or in a list): it refuses
  malformed fields and ids, checks the postcode, house number and box against the country profile, takes the time
  zone from the profile or the municipality (D33: a venue's zone comes from its address), renders `Lines` from the
  profile's template, and asks the house-number validator without ever blocking. In free-text mode it checks the
  shape only and marks the address unverified.
- **Zone names (D36 step 1):** the client registers the core's `ISparkTimeZoneNameProvider`. `ISparkTimeZoneNames`
  then labels `Europe/Brussels` "Brussel" in Dutch; where CLDR states no city, the core derives it from the id.
- **Release sync (RD6):** a conditional check on `/api/v1/releases/latest`; a changed release is read and verified (file
  hashes, digests, each document's hash, and the Ed25519 signature when `PublicKey` is set: then an unsigned release is
  refused too). The delta from the release this database holds is applied when the manifest carries one, else the full
  files, which are refused when they drop an id the database holds. Writes skip unchanged documents (a first load goes
  through BulkInsert), so re-applying writes nothing. Every run then compares, per release file, the digest of what the
  database holds with the manifest's (the **periodic checksum**) and re-imports a file that differs in full.
  `refdata/state` is written last. Streets stay on the live API.
- **Read-only local copies (plan R10):** the client package's library layer (alias `reference-data-client`) declares
  `RefCountry`, `RefPlace` and `RefPostalCode` with read-only attributes and their queries; no write right ships, and
  only the sync writes them. Grant `QueryRead/RefPlace` (and the others) in your own `security.json` if users may browse
  them. An instance references this package and does not declare those types itself, so a host that is both instance
  and consumer composes each type once.
- **Country names on the server** (mails, PDFs): `CldrNames.Territory("BE", "nl")`, a pinned CLDR subset (Q-R18).

## Angular

```ts
import { provideReferenceData } from '@mintplayer/ng-spark/reference-data';
import { sparkAddressRenderers } from '@mintplayer/ng-spark/reference-data/spark';

provideReferenceData({ baseUrl: 'https://refdata.example.org' }),   // null = free-text mode; no default URL
provideSparkAttributeRenderers([...sparkAddressRenderers]),
```

- Without `provideReferenceData` the address form fails loudly (NG0201): there is no default URL.
- Requests use `new HttpClient(inject(HttpBackend))`: no interceptors, so no credentials go to a public origin and no
  error ever redirects to a sign-in page.
- `<spark-address-input [(value)]="address" lang="nl" />` (also a form control): a country picker named with
  `Intl.DisplayNames` and sorted with `Intl.Collator`, a postal-locality typeahead, a street typeahead inside the picked
  municipality, house number and box; debounced, with `switchMap` cancellation; "not listed" switches to free text.
- On a model attribute: `"renderer": "spark-address"` on a single AsDetail `Address` gives the generic pages the
  address editor, the rendered lines on the detail page and one line in grids.
- `displayPlaceName(...)` is the TypeScript twin of the server's display rule; `renderAddressLines(...)` the twin of the
  server's line rendering.
