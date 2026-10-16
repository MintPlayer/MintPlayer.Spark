# Source: S13 apply fixtures

- `<eco>/*.diff`, `<eco>/conflict*`, `<eco>/SOURCE.md`: copied from spike S13 (`spikes/s13/fixtures/apply`,
  2026-10-15), local paths scrubbed to `<SPIKES>` / `<HOME>`, the host name to `<HOST>`.
- `<eco>/base/**`: the sample's base manifests, re-created byte for byte with the SAME `printf`/`sed`
  commands as `spikes/s13/run-*.sh` (scratchpad `make-bases.sh`, 2026-10-16). The golden tests edit
  these with the adapters' pure edit functions and compare the changed lines with the `-`/`+` pairs
  of the corresponding real diff (R13 criterion 1: the manifest patch is exactly the planned token).

The Dependabot CLI captures are not copied: D-S13b dropped that fallback.
