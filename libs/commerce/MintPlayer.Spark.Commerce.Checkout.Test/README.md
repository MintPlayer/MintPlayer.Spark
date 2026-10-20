# MintPlayer.Spark.Commerce.Checkout.Test

Test adapters for `MintPlayer.Spark.Commerce.Checkout`, for E2E tests and demos. **Never for production.**

- `spark.AddTestInsurance()`: an in-process ticket-insurance provider (GC4). Quotes are deterministic (5 % of the insured
  amount, at least 1.00; the quote id hashes the insured lines), policies are kept in memory, `FailBinding` simulates an
  insurer that is down. The cover is offered at the last step after the main price and is never pre-ticked.

See `docs/guide-commerce-checkout.md`.
