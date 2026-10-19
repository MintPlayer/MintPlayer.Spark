# MintPlayer.Spark.ReferenceData.Sources.Poland

The Polish reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8).

```csharp
spark.AddReferenceData().AddPoland();
```

| Source id | Dataset | Default schedule (UTC) | Change check |
|---|---|---|---|
| `pl-teryt` | GUS TERYT [full files](https://eteryt.stat.gov.pl/eTeryt/rejestr_teryt/udostepnianie_danych/baza_teryt/uzytkownicy_indywidualni/pobieranie/pliki_pelne.aspx): TERC, SIMC, ULIC ("urzędowy", CSV in ZIP, ≈12 MB together) | `0 0 5 * * 1` | the three files are downloaded and their rows hashed without `STAN_NA` (ULIC's `STAN_NA` is the download day); the **version is the retrieval date** |
| `pl-prg` | GUGiK PRG [`POLSKA.zip`](https://opendata.geoportal.gov.pl/prg/adresy/PunktyAdresowe/POLSKA.zip) (≈564 MB), attribute table of the address points | `0 0 3 * * 0` | `Content-Length` only: the server sends no `ETag`/`Last-Modified` and ignores range requests (checked 2026-10-19), a weak validator |

The TERYT page is an ASP.NET form: each file is a postback of its button with the page's hidden fields. The change service needs an account and is not used.

## Credit lines (PRD §4.12)

- TERYT: "Źródło: GUS, TERYT, CC BY 4.0, stan na <data>": an import sets the retrieval date (the source version) in the release's attribution (`SourceInput.Attribution`); the fixture build carries the generic line.
- PRG: "Źródło: Państwowy Rejestr Granic (PRG), GUGiK" — "free for any use", no licence name. **Postcodes are flagged as not validated by Poczta Polska** in this credit line (the PNA list is sold and not used).

## Mapping and catches

- TERC: voivodeships `places/pl/terc/{WOJ}` (written in capitals: "POMORSKIE" → "Pomorskie"), counties `…/{WOJ}{POW}`, gminy `…/{WOJ}{POW}{GMI}{RODZ}` for kinds 1–3; kinds 4–5 (town and rural parts of an urban-rural gmina) are not units; 8 (Warsaw districts) and 9 (delegations) are `pl:district` under their city. A city with county rights is a county and a gmina (Sopot 2264 and 2264011).
- SIMC: `places/pl/simc/{SYM}`; a part of a locality (`SYMPOD` ≠ `SYM`, e.g. Sopot's Przylesie) lies under its locality; `RM` kept as code `simc-rm`.
- ULIC: `SYM_UL` is a national street-*name* key, not a street ("Bałtycka" 00656 in Sopot and in Hel), so streets are `streets/pl/ulic/{SYM}-{SYM_UL}`. Names: "ul. Antoniego Abrahama" (official, with `CECHA` unless "inne"), "Antoniego Abrahama" and "Abrahama" (common).
- PRG: the attribute table (`PRG_PunktyAdresowe_POLSKA.zip.dbf`, ≈7 GB) is compressed with **Deflate64**; .NET's `ZipArchive` reads it, Python's `zipfile` does not. It is streamed from the archive, never extracted. Per point: `KOD_POCZT`, `ID_ULIC` (= `SYM_UL`), `ID_SIMC`, `TERYT_GMI`; house numbers are not kept (RD5).
- Not imported: minority-language names (1,305 localities; not in these files), history (change files need an account; `HistoryKnownSince` unset).

## Fixtures (real cuts, RS8; 2026-10-19)

| File | Rule |
|---|---|
| `TERC_Urzedowy_2026-10-19.cut.csv` | voivodeship 22 (pomorskie), counties 2211 (pucki) and 2264 (Sopot), gminy Hel 2211011 and Sopot 2264011 |
| `SIMC_Urzedowy_2026-10-19.cut.csv` | the localities of those gminy: Hel, Sopot and Sopot's parts |
| `ULIC_Urzedowy_2026-10-19.cut.csv` | Sopot: Abrahama, Chopina, Królowej Jadwigi, Młyńska, Bałtycka, Helska; Hel: Bałtycka, Helska, Rybacka |
| `PRG_PunktyAdresowe_POLSKA.cut.dbf` | streamed out of `POLSKA.zip` (Deflate64): the first two points of each of those street keys within the two gminy (14 records; header and field layout unchanged, record count patched) |

Raw bytes kept (UTF-8 BOM, `;`, CRLF; dBASE III). Cut by `scratchpad/f4/scripts/cut_pl.py` and `prgcut.ps1`. Ledger: `Ledger/pl.ndjson`, filled by `--refdata-build-seed`.
