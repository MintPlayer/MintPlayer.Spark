# Invoicing fixtures (spike S14, round 4; round 5 GA4)

**Every fixture here is doc-derived** (D37): written from the party's public documentation, not recorded from a live
sandbox. No live calls are made by the tests.

| File | Derived from | Notes |
|---|---|---|
| `Vies/check-vat-number.valid.json` | VIES REST API, `POST /check-vat-number` (https://ec.europa.eu/taxation_customs/vies/#/technical-information, read 2026-10-19) | Field names as documented; `---` marks an unprocessed trader field. Example company, not a real one |
| `Vies/check-vat-number.invalid.json` | same | `valid: false` |
| `Vies/check-vat-number.ms-unavailable.json` | same, error shape `errorWrappers[].error` | `MS_UNAVAILABLE` is no answer: the id stays unverified |
| `Storecove/document_submissions.200.json` | https://www.storecove.com/docs/ (submission answers `{ "guid": … }`) | |
| `Storecove/document_submissions.422.json` | same (422 with a list of `{source, details}`) | The detail text is illustrative |
| `Storecove/discovery_receives.ok.json` | same (`discovery/receives` answers `code` and `email`) | |
| `Billit/sendxml.200.json` | https://docs.billit.be/docs/send-ubl-to-peppol-1 ("When Status Code is 200, the InboxItemID is returned") | The JSON wrapping is assumed; the adapter also reads a bare value |

The UBL itself is validated by the library's EN 16931 rule subset (`En16931RuleSubset`). The official CEN/TC 434 and
OpenPeppol Schematron artefacts are XSLT 2.0 and not vendored (see the guide, "Validation").
