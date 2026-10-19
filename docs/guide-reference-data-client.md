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
- **Release sync (RD6):** a conditional check on `/api/v1/releases/latest`; a changed release is read, verified (file
  hashes, digests, each document's hash), refused when it drops an id the database holds, and applied skip-if-unchanged,
  so re-applying it writes nothing. `refdata/state` is written last. Streets stay on the live API.
- **Country names on the server** (mails, PDFs): `CldrNames.Territory("BE", "nl")`, a pinned CLDR subset (Q-R18).

Not built yet: deltas, the periodic per-collection checksum, signature verification (RS17), and the read-only model
layer for the local copies (consumers reference them by id).

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
