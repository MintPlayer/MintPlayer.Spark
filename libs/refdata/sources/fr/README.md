# MintPlayer.Spark.ReferenceData.Sources.France

The French reference-data adapter (docs/reference_data_PRD.md §4.4, plan R8): INSEE regions, départements and communes
since 1943 with their mouvements (mergers, communes nouvelles, déléguées and associées, renames, recodes, code reuse),
BAN streets and postcodes, and La Poste's postal localities, with the French country profile (VAT territories
included), the id ledger and real sample fixtures. It replaces the global baseline's hand-written FR profile.

```csharp
spark.AddReferenceData().AddFrance();
```

## Sources

| Id | Dataset | Licence | Default schedule (UTC) | Change check |
|---|---|---|---|---|
| `fr-cog` | INSEE [Code officiel géographique](https://www.insee.fr/fr/information/2560452): `v_region_{yyyy}`, `v_departement_{yyyy}`, `v_commune_{yyyy}`, `v_commune_depuis_1943`, `v_mvt_commune_{yyyy}` (CSV, yearly on 1 January) | Licence Ouverte 2.0 | `0 0 5 * * 1` | the millésime on INSEE's COG page ("Millésime 2026 : Téléchargement des fichiers") |
| `fr-ban` | [Base Adresse Nationale](https://adresse.data.gouv.fr/donnees-nationales), `adresses-{dep}.csv.gz` per département (continuous) | Licence Ouverte 2.0 | `0 30 5 * * 0` | `ETag`/`Last-Modified` of the national file |
| `fr-laposte` | La Poste [base officielle des codes postaux](https://www.data.gouv.fr/fr/datasets/base-officielle-des-codes-postaux/) (`laposte-hexasmal`, ≈1.5 MB, irregular) | Licence Ouverte 2.0 (data.gouv.fr, checked 2026-10-19) | `0 15 5 * * 1` | `Last-Modified` |

Credit lines: "Source : Insee, Code officiel géographique" with the millésime (INSEE asks for the date of last update and
forbids implied endorsement); "Source : Base Adresse Nationale"; "Source : La Poste, base officielle des codes postaux";
each with a statement that the data was modified.

## Catches

- **`api-adresse.data.gouv.fr` is not used** (announced decommissioned end of January 2026; its successor allows
  50 requests/s per IP): the adapter reads BAN files only.
- **An INSEE code is not a territory id.** Codes are reused by design: when a commune nouvelle takes the code of a
  commune that carries on as its commune déléguée (MOD 32 with `COM 01015 → COMD 01015`), the new commune is a new place
  `places/fr/insee/01015@2016-01-01` (Arboys en Bugey) and `places/fr/insee/01015` stays Arbignieu (PRD §4.3).
- **Recodes keep the id** (MOD 41/50): Ajaccio is `places/fr/insee/20004` with codes `20004` until 1976 and `2A004`
  after. Corsica's `2A`/`2B` are not numeric.
- **Départements of the edition only**: a commune's département is the current one while its code is current, else the
  one its code names (an INSEE code embeds its département; a département change re-codes the commune). Corse (`20`,
  before 1976) is not in the edition: such periods have no parent and a warning (never invented).
- Communes déléguées (MOD 32) and associées (MOD 33) continue one level down; MOD 34/35 end them into their commune
  (Rignat, associée of Bohas-Meyriat-Rignat from 1974, ended on 2026-01-01).
- **BAN toponym UUIDs**: the PRD keys streets on `id_ban_toponyme`, never `cle_interop` (it embeds the commune code).
  ⚠️ The national per-département CSV of 2026-10-19 carries no UUID column (`csv` and `csv-bal` alike; `uid_adresse`
  is empty). The mapper uses `id_ban_toponyme` when present and otherwise falls back to the toponym key (`id_fantoir`,
  else the first two parts of the address `id`), recorded as scheme `ban-voie`. That key embeds the commune code, so a
  commune merger can re-key streets: the ledger records both and a re-keyed street becomes a new id. Revisit when BAN
  publishes the UUIDs in the files.
- Commune ↔ postcode is many-to-many. Postal labels (`libelle_acheminement`, La Poste) are upper case without accents
  and abbreviate Saint (`ST BOIS`): they are matched to commune and déléguée names on a folded key, else title-cased.
- The overseas départements (971–976) get their own zones (Q-R10); the collectivities 975–989 have their own ISO codes
  and are not part of `fr`. BAN quality depends on the communes' Bases Adresses Locales.
- La Poste's file is ISO-8859-1 with a `#` before its first column name.

## Fixtures (real samples, RS8; cut 2026-10-19)

| File | Cut from | Rule |
|---|---|---|
| `v_region_2026.cut.csv`, `v_departement_2026.cut.csv` | INSEE COG 2026 (https://www.insee.fr/fr/information/8740222) | regions 84 and 94, départements 01 and 2A |
| `v_commune_2026.cut.csv` | same | the communes 01015, 01053, 01245, 2A004 and their communes déléguées |
| `v_commune_depuis_1943.cut.csv` | same | every row of 01015, 01048, 01053, 01245, 01324, 01340, 20004, 2A004 |
| `v_mvt_commune_2026.cut.csv` | same | every mouvement naming those codes |
| `adresses-01.cut.csv`, `adresses-2A.cut.csv` | BAN `latest/csv/adresses-01.csv.gz` and `adresses-2A.csv.gz` (2026-10-19) | the first address row of a few streets per commune (rows with and without `id_fantoir`) |
| `laposte_hexasmal.cut.csv` | La Poste `laposte-hexasmal` raw CSV (Last-Modified 2026-10-08) | the rows of 01015, 01053, 01245, 2A004 (ISO-8859-1 kept) |

Ledger: `Ledger/fr.ndjson`, filled by `--refdata-build-seed` (docs/guide-reference-data.md).
