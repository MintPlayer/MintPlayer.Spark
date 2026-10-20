# MintPlayer.Spark.ReferenceData.Sources.Tedb

EU VAT-rate suggestions for MintPlayer.Spark reference data (commerce GC10): the European Commission's TEDB (Taxes in Europe Database) VAT retrieval service, mapped to VatRateSuggestion documents. Suggestions only: commerce shows them, the host sets its own rates.

Fixtures (where present) are real samples cut from the publisher's files; their licences and credit lines are in the source classes and at `/api/v1/sources`.

## Fixture

`retrieveVatRates-BE-DE-FR-LU-NL-2026-10-01.cut.xml`: a recorded `retrieveVatRates` response (situationOn 2026-10-01) for BE, DE, FR, LU and NL, cut to the standard results and the first two reduced results per member state, CN codes removed. The licence (Commission reuse notice) is to be confirmed in RS23.
