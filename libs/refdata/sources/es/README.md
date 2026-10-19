# MintPlayer.Spark.ReferenceData.Sources.Spain

The Spanish reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8).

```csharp
spark.AddReferenceData().AddSpain();
```

| Source id | Dataset | Default schedule (UTC) | Change check |
|---|---|---|---|
| `es-ine` | INE [`{yy}codmun.xlsx`](https://www.ine.es/daco/daco42/codmun/26codmun.xlsx) (one sheet per province, January) + [`cod_ccaa_provincia.htm`](https://www.ine.es/daco/daco42/codmun/cod_ccaa_provincia.htm) | `0 0 5 * * 1` | next year's file name probed, then `ETag`/`Last-Modified` |
| `es-callejero` | INE Callejero [`caj_esp_{MM}{yyyy}.zip`](https://www.ine.es/dyngs/DAB/es/index.htm?cid=1390) (≈50 MB; `UP` and `VIAS` read, `TRAM`/`PSEU`/`SECC` not) + `TiposVia.csv` | `0 0 5 * * 2` | the dated file name on the page |
| `es-callejero-var` | INE `Var_Callejero_{MMyy}_{MMyy}.zip` (street altas, bajas, modifications) | `0 0 5 * * 2` | the dated file name on the page |
| `es-cartociudad` | CNIG [CartoCiudad](https://centrodedescargas.cnig.es/CentroDescargas/buscadorCatalogo.do?codFamilia=02122) attribute table (`COD_POSTAL`, `INE_MUN`) | — | **none**: CNIG offers the files only behind its download-centre form (checked 2026-10-19); the source never queues itself and its fetch refuses with a message |

## Credit lines (PRD §4.12)

- INE: "Elaboración propia con datos extraídos del sitio web del INE: www.ine.es" (INE is a source, never an endorser).
- CartoCiudad (IGN/CNIG): **"Obra derivada de CartoCiudad CC-BY 4.0 scne.es"**. IGN requires the formula next to the data *and in the service's own metadata*. It is the source's `Name`, so the compact attribution in **every API response carrying ES data** holds it, and `/api/v1/sources` (the service self-description) lists the full line. The instance must not shorten attributions for ES data. A CNIG footer says CC BY-SA: the CC BY 4.0 product licence is followed and the conflict noted.

## Mapping and catches

- Ids: communities `places/es/ine-ccaa/{CODAUTO}`, provinces `places/es/ine-prov/{CPRO}`, municipalities `places/es/ine/{CPRO}{CMUN}` (INE keeps codes since 1970; new ones start with 9; an extinct code returns only with its re-created municipality), population entities `places/es/ine-ue/{CPRO}{CMUN}{EC}{ES}`, streets `streets/es/ine/{CPRO}{CMUN}-{CVIA}`, postcodes `postalcodes/es/{code}`.
- Names: slash-joined official names split into the co-official language (eu, gl, ca) and Spanish ("Donostia/San Sebastián"); inverted articles and titles restored ("Coruña, A" → "A Coruña", "Palmas de Gran Canaria, Las" → "Las Palmas de Gran Canaria", "Asturias, Principado de" → "Principado de Asturias"). The Callejero is upper case: entity and street names are title-cased (generated), with the sorting particle moved to the front ("AFRICA (DE)" → "de Africa"; INE drops some accents in street names).
- Streets: "{type} {name}" with INE's type table, unless the name already carries the type (Basque "Kale Nagusia", "Apezpiku kalea"). Periods come from the variation files (alta starts, baja ends, modification dates the current name). A baja of a street the current file no longer lists is reported, not imported.
- The variation files match neither published design (`Dis_nuevo.xlsx`, `Dis_antiguo.xlsx`): positions were measured on `Var_Callejero_0126_0726` (old CVIA 6–10, FVAR 14–21, CVAR 26, new CVIA 27–31, TVIA 32–36, NVIA 38–87). In the full `UP` file the municipality name sits in NMUN50 (95–144); NMUN is blank.
- Postcodes come from CartoCiudad, never from the Callejero's `TRAM` (possibly Correos-derived; Correos' list is closed). Street ↔ postcode links need CartoCiudad's portal layer and are not built yet.
- Zones (Q-R10): Canary Islands `Atlantic/Canary`, Ceuta and Melilla `Africa/Ceuta`, else `Europe/Madrid`, by province.
- VAT territories (commerce GA4): Canary Islands (35xxx, 38xxx) Art. 6(1), Ceuta (51xxx) and Melilla (52xxx) Art. 6(2) of Directive 2006/112/EC; article letters **not confirmed on EUR-Lex**.
- History: no municipal history is imported (INE's alterations since 1842 have no stable machine-readable URL); `HistoryKnownSince` is left unset. Catastro is excluded (licence conflict).

## Fixtures (RS8)

| File | Provenance | Rule |
|---|---|---|
| `26codmun.cut.xlsx` | real cut of `26codmun.xlsx` (2026-10-19) | sheets 15, 20, 35, 51; title rows and A Coruña 15030, Donostia/San Sebastián 20069, Hondarribia 20036, Las Palmas de Gran Canaria 35016, Ceuta 51001; workbook rebuilt with the same parts, shared strings re-indexed, `x14ac:` attributes dropped |
| `cod_ccaa_provincia.cut.htm` | real cut of the INE page (2026-10-19) | the table head and the rows of provinces 15, 20, 31, 35, 51 (bytes kept, ISO-8859-1, HTML entities) |
| `UP.D260630.G260702.cut` | real cut of `caj_esp_072026.zip` | municipality rows and the first two singular entities (with their núcleo rows) of the five municipalities |
| `VIAS.D260630.G260702.cut` | real cut of `caj_esp_072026.zip` | 2–3 streets per municipality |
| `VIAS.D260101.A260630.cut` | real cut of `Var_Callejero_0126_0726.zip` | the first eight variation lines of the five municipalities (altas, a baja, a modification) |
| `TiposVia.csv` | INE `TiposVia.csv`, whole | street-type table |
| `CARTOCIUDAD_codigo_postal.docderived.dbf` | **doc-derived** (CartoCiudad product specification, https://www.cartociudad.es; field names unverified until the first manual download) | dBASE III, `COD_POSTAL` + `INE_MUN`, real postcodes of the five municipalities |

Cut by `scratchpad/f4/scripts/cut_es.py` and `make_dbf.py`. Ledger: `Ledger/es.ndjson`, filled by `--refdata-build-seed`.
