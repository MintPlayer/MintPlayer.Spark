# Security Audit — Test Run Results (master, 2026-04-20)

**Companion to:** [PRD-SecurityAudit.md](PRD-SecurityAudit.md)
**Commit under test:** `ea596e9` (master HEAD at audit time), tests at `ea368af` on `feat/security-audit`
**Run:** TRX at `MintPlayer.Spark.E2E.Tests/TestResults/security.trx`

## Summary

| Metric | Count |
|--------|-------|
| Total tests | 28 |
| Passed | 7 |
| Failed | 18 |
| Skipped | 3 |

## Triage refinements applied

- **H-1** — contract is *filter*, not *refuse*. Unauth caller gets the subset their effective principal (Everyone group) has `Query` rights on.
- **L-3** — demo-only. Framework stays out; each demo app wires `AddRateLimiter()` in its own `Program.cs`.

## Full outcome matrix (parsed from TRX)

### Vulnerabilities reproduced (14 real failing tests)

| Finding | Test | Notes |
|---------|------|-------|
| H-1 | `MetadataEndpointAuth.Unauthenticated_GET_spark_queries_includes_only_queries_visible_to_anonymous_callers` | Full catalogue returned to anon |
| H-1 | `MetadataEndpointAuth.Unauthenticated_GET_spark_types_includes_only_types_visible_to_anonymous_callers` | Full type schemas returned to anon |
| H-1 | `MetadataEndpointAuth.Unauthenticated_GET_spark_queries_id_for_protected_query_is_refused` | Direct access to protected query definition returns 200 |
| H-1 | `MetadataEndpointAuth.Unauthenticated_GET_spark_aliases_includes_only_aliases_visible_to_anonymous_callers` | Protected aliases leak |
| M-5 | `SortInjection.Sort_by_nonexistent_property_is_rejected` | Framework silently ignores bad column |
| M-5 | `SortInjection.Sort_by_metadata_property_on_projection_is_rejected` | `Id` as sort column accepted |
| L-2 | `XsrfCookieFlag.XSRF_TOKEN_cookie_carries_Secure_attribute_over_https` | No `Secure` flag even over HTTPS |
| L-3 | `RateLimit.Rapid_unauthenticated_bursts_trigger_429_Too_Many_Requests` | 200 requests, zero 429s — no rate limiter |
| H-5 | `ReturnUrlValidation.Login_with_protocol_relative_returnUrl_does_not_navigate_off_site` | — |
| H-5 | `ReturnUrlValidation.Login_with_absolute_http_returnUrl_does_not_navigate_off_site` | — |
| H-5 | `ReturnUrlValidation.Login_with_absolute_https_returnUrl_does_not_navigate_off_site` | — |
| H-5 | `ReturnUrlValidation.Login_with_javascript_uri_returnUrl_does_not_execute_script` | — |
| H-5 | `ReturnUrlValidation.Login_with_backslash_authority_returnUrl_does_not_navigate_off_site` | — |
| H-5 | `ReturnUrlValidation.Login_with_whitespace_prefixed_returnUrl_does_not_navigate_off_site` | All 6 H-5 tests fail uniformly — strongly suggests a test-infrastructure issue (login form not being submitted) rather than all 6 attack vectors genuinely succeeding. Needs investigation before counting these as confirmed vulnerabilities |

### Test-setup bugs (4 failing tests — blocking, not vulnerability evidence)

| Finding | Test | Issue |
|---------|------|-------|
| L-2 | `XsrfCookieFlag.XSRF_TOKEN_cookie_has_SameSite_Strict` | `SparkMiddleware.cs:191-196` sets `SameSite=Strict`; the assertion shape is probably wrong (cookie attribute casing?). Test needs fixing |
| L-7a | `AttributeWriteProtection.Update_with_IsReadOnly_attribute_in_body_does_not_modify_field` | POST /spark/po/Car returns 500 at setup — not yet debugged |
| L-7b | `AttributeWriteProtection.Update_cannot_escalate_via_unknown_attribute_name` | Same 500 as above |
| M-7 | `Concurrency.Concurrent_update_with_stale_version_is_rejected` | Same 500 as above |

### Already-secure behaviour (7 passing tests)

| Finding | Test | Implication |
|---------|------|-------------|
| M-1 | `PermissionsEndpointAuth.Unauthenticated_GET_permissions_for_Car_reports_no_access` | `/spark/permissions/{type}` already returns `{canRead:false, ...}` to anon |
| M-1 | `PermissionsEndpointAuth.Unauthenticated_GET_permissions_for_Company_reports_read_but_no_write` | Company anonymous read is honored, mutations correctly denied |
| M-3 | `NotFoundVsForbidden.Nonexistent_id_and_forbidden_id_return_the_same_status` | 404/403 already indistinguishable in the tested path |
| M-5 | `SortInjection.Sort_with_malformed_direction_does_not_500` | Malformed direction falls through to default (no 500) |
| M-6 | `ErrorLeakage.Malformed_entityTypeId_does_not_leak_stack_trace_or_internal_types` | Clean error body |
| M-6 | `ErrorLeakage.Malformed_id_on_get_does_not_leak_internals` | Clean error body |
| M-6 | `ErrorLeakage.Bad_lookup_reference_key_does_not_leak_internals` | Clean error body |

**Implication for remediation scope:** M-1 and M-6 are already secure in Fleet's setup. The tests stay as regression protection, but no framework change is required for those findings.

### Skipped pending row-level filter hook (3)

| Finding | Test | Blocker |
|---------|------|---------|
| H-2 | `RowLevelAuthz.User_B_cannot_list_User_As_private_cars` | Row-level filter hook not yet added |
| H-2 | `RowLevelAuthz.User_B_cannot_read_User_As_private_car_by_id` | Same |
| H-3 | `RowLevelAuthz.User_B_cannot_execute_child_query_with_User_As_parent_id` | Same |

## Revised remediation priority

Based on what's actually broken:

1. **H-1** (4 tests) — filter `/spark/queries`, `/spark/types`, `/spark/queries/{id}`, `/spark/aliases` by caller's `Query` rights.
2. **Test-setup fix** (4 tests) — unblock L-7a/L-7b/M-7 by debugging the 500 on admin Car creation, and fix the L-2 SameSite assertion shape.
3. **H-5 investigation** (6 tests) — verify whether all 6 are real vulnerabilities or a test-infrastructure uniformity problem. If the login form submission isn't going through, the test never reaches the router and every URL shape fails identically.
4. **L-2 Secure flag** (1 test) — one-line change in `SparkMiddleware.cs:191-196`.
5. **M-5** (2 tests) — allow-list sort columns against the query's declared attribute set.
6. **M-7 / L-7a / L-7b** — after the setup-fix, real assertion runs. M-7 probably needs the concurrency fix; L-7 likely confirms the write-path doesn't enforce `IsReadOnly`/`IsVisible`.
7. **L-3** (1 test) — wire `AddRateLimiter()` in `Demo/Fleet/Fleet/Program.cs`.
8. **H-4, H-2, H-3** — framework-level hooks per PRD §6.

## Out of scope for this PR

- **H-4** fail-closed-without-authz — needs a separate host fixture.
- **M-2** JWT tampering — Fleet is cookie-based; add when IdentityProvider lands.
- **M-4 / L-4** marker attributes — need fix-side Actions class fixture.
