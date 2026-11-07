# A constant's value is not API (#576) — plan

Spec: [coverage_api_hash_values_PRD.md](coverage_api_hash_values_PRD.md). One pull request, branch
`fix/issue576-api-hash-literal-consts` from master `ad884e653`. Tests run once, at the end.

## Status (2026-11-07)

All milestones done. Affected test classes green locally (Api, Build, Releases, Validation, Versions namespaces of
`CodeCoverage.Versioning.Tests`: 346 tests, one wrong expected line fixed, then green).

## Spikes

- [x] **S1** — which initializers `tsc` writes into `.d.ts` (PRD §4). Result: string/number/bigint/boolean on `const`
  declarators and `readonly` properties only.
- [x] **S2** — hash the published ui-core 1.7.0 and 1.8.0 with listings 1 and 2 (PRD §4). Listing 1 = the tags;
  listing 2 equal.

## Milestones

- [x] **M1 — Listing 2 (D1).** `TypeScriptApiListing.WidenLiteralInitializers`, applied after comments are stripped and
  before whitespace is collapsed (the CSS holds `;`, `{` and `}`, which the statement splitter would cut on). String
  and template literals are matched first and kept, so declaration-like text inside a string is never rewritten.
  `TypeScriptApiListing.Version = 2`.
  Tests: `A_constant_lists_by_its_type_so_its_value_does_not_change_the_hash` (the ui-core shape),
  `Each_literal_the_compiler_emits_becomes_its_type` (S1's output),
  `Declared_literal_types_type_aliases_enum_members_and_text_inside_strings_stay`, `The_hash_names_listing_version_2`.
- [x] **M2 — Versioned hash (D2).** `Api/ApiHash.cs` (`Of`, `Listing`, `Comparable`); `DotnetApiListing.Hash` delegates
  to it with `Version = 1` (same bytes as before); `Identifiers.IsApiHash` accepts `v2:`–`v99:`; validation messages say
  `[v<N>:]sha256:<64 hex>`.
  Tests: `IdentifiersTests.Api_hashes` (new rows).
- [x] **M3 — Compare like with like (D2).** `BuildManifest` asks the registry when the tag's hash is absent or not
  `Comparable`; `ReleasePlanner` uses the tag's hash only when `Comparable`, else `PreviousApiHash`.
  Tests: `BuildManifestTests.A_tag_hashed_by_another_listing_version_asks_the_registry`,
  `ReleasePlannerTests.A_tag_hash_from_another_listing_version_is_replaced_by_the_registry_rehash` (patch with a matching
  rehash, minor without), `A_tag_hash_that_differs_only_by_its_listing_prefix_is_not_equal`.
- [x] **M4 — Docs.** This plan and PRD; pointer from the versioning PRD's D6.

## After merge

- The server deploys from master. mintplayer-ng-bootstrap gets the fix when its `versioning-action` pin moves to a
  release containing this change; file an issue there rather than editing it from here.
