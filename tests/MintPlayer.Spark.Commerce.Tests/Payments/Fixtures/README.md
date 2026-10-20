# Payment provider fixtures (spikes S5 and S6, fixture-based)

**Every file here is DOC-DERIVED, not recorded.** Owner round 4 (commerce PRD §13): until the owner's Mollie and Stripe
development accounts exist, the adapters are tested against request/response shapes taken from the providers'
**public API documentation**. Each file names its source page in `_fixture.source`. Ids, amounts and timestamps were
adapted so the files fit together (the payment a refund names exists, and so on); field names, nesting and value
formats follow the documentation.

When the sandbox accounts exist, replace each file by a recording of the same call (scrub ids that identify the
account) and set `_fixture.recorded` to `true`. A field the recording has that the fixture lacked is a finding:
check the parser.

Known gaps, because the public documentation does not show them:

- **Mollie, refund refused for insufficient balance** (`Mollie/refund-insufficient-balance.json`): Mollie documents the
  error format (`status`, `title`, `detail`, `field`) but not this case's `detail` text. The adapter matches "balance"
  or "insufficient" loosely; a recording must confirm the wording.
- **Mollie application fees in the platform's settlement**: not shown in the settlement examples; the platform's fee
  lines are therefore not imported from Mollie yet (they are in the fee ledger from the payments themselves).
- **Stripe `insufficient_funds` refund failure** on a connected account: the `failure_reason` value is documented on
  the Refund object; the full event below is assembled from the Refund object and Event object docs.
