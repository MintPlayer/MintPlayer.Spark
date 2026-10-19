# MintPlayer.Spark.ReferenceData.Sources.Denmark

The Danish reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8). Register it with
`spark.AddReferenceData().AddDenmark()`.

## Sources

| Id | Source | Licence and credit line | Default schedule | Change check |
|---|---|---|---|---|
| `dk-dagi` | DAGI administrative units (regions, municipalities) via Datafordeleren file downloads (JSON in ZIP; published on Sundays) | CC BY 4.0 (downloads from 2024-05-16); "(CC BY 4.0) KDS", **"DMI, Kort og Grunddata" from 2026-12-01** | `0 0 6 * * 1` | `ETag`/`Last-Modified` |
| `dk-dar` | DAR (Danmarks Adresseregister): named roads, their municipality parts, postcodes and the road ↔ postcode relation (a full file every Saturday night; deltas not used) | as above | `0 0 5 * * 0` | `ETag`/`Last-Modified` |

- **API key (host secret).** Datafordeler needs an account, and after **15 January 2027** only API-key or OAuth
  access. Set `Spark:ReferenceData:Sources:dk-dar:ApiKey` (and optionally `dk-dagi:ApiKey`) in user-secrets. The key
  is read when a download runs, sent as a request header, and never written to a URL, the source state, a log or a
  document. Without it, the check fails with a message that names the setting (never the value).
- **The credit line follows the data version's date, not the clock** (K10): `DenmarkLicences.For(dataset, date)`;
  the fixture is dated 2026-10-19 and credits "(CC BY 4.0) KDS".
- DAWA is closed (HTTP 410) and is not used. Greenland and the Faroe Islands are not included (own ISO codes).
- ⚠️ Unverified (Datafordeler answers 401 without an account): the file-download query string and the key header
  name in `DatafordelerSource` (RS8).

## Mapping and catches

- **Bitemporal registers:** only an object's current registration (`registreringTil` empty) is read; its effective
  time (`virkningFra`/`virkningTil`) is the period. Status 3 (in force) and 4 (retired) are kept.
- Ids: regions `places/dk/region/{regionskode}`, municipalities `places/dk/kommune/{kommunekode}`, postcodes
  `postalcodes/dk/{postnr}`, roads `streets/dk/dar/{id_lokalId}` (the DAR UUID, stable across renames). A road in
  several municipalities (Vesterbrogade) has one period per municipality and one `vejkode` per part.
- The DAR file is streamed object by object (`JsonArrayStream`); memory stays flat.
- **History known since** 2007-01-01 (the municipal reform; DAGI's current units). The endonym "Danmark" is
  hand-authored (the embedded CLDR subset has de/en/fr/nl only).

## Fixtures

| File | Provenance |
|---|---|
| `DAGI_Totaludtraek.docderived.json` | **doc-derived** from Datafordeler's DAGI documentation (https://confluence.sdfi.dk/ — DAGI object types `Regionsinddeling`, `Kommuneinddeling`; bitemporal fields). Regions 1083/1084 and municipalities 0101 København, 0147 Frederiksberg, 0461 Odense are real codes and names; the UUIDs and registration times are **illustrative**; one superseded registration shows the bitemporal filter |
| `DAR_Totaludtraek.docderived.json` | **doc-derived** from Datafordeler's DAR documentation (object types `Postnummer`, `NavngivenVej`, `NavngivenVejKommunedel`, `NavngivenVejPostnummer`). Postcodes 1620, 1850, 5000 and the road names are real; the UUIDs and the vejkoder are **illustrative** |

The download could not be made from here (an account is required, 2026-10-19). Replace both with real cuts (RS8).
Ledger: `Ledger/dk.ndjson`.
