# PRD — a private repository must be indistinguishable from one that does not exist

**Status:** In progress · **Date:** 2026-09-27 · **Plan:** [coverage_repo_existence_parity_plan.md](coverage_repo_existence_parity_plan.md)
**Issue:** [#453](https://github.com/MintPlayer/MintPlayer.Spark/issues/453) · **App:** `apps/CodeCoverage` (production, coverage.mintplayer.com) plus `libs/spark`, `libs/node_packages/ng-spark*`
**Origin:** Five-agent investigation, 2026-09-27. Two observed production and a local host through a real
browser (anonymous), two read the code, one diffed private-vs-missing responses byte for byte.

---

## 1. The report, and what it turned out to be

> Visiting `/po/repository/Repositories%2Fgithub%2F680909514` briefly renders all repository information
> before a redirect [to login]. The page may contain private information.

**The repository in the report is public** (`MintPlayer/MintPlayer.AspNetCore.Tools`, `private:false`).
What the reporter saw is:

1. `POST /spark/po/load` answers an anonymous caller **200** with the repository — correct, anonymous may
   read public repositories (`security.json` grants anonymous Read on Repository; the row filter is
   `!IsPrivate || OwnerKey ∈ viewer's owners`, `Services/RepositoryVisibility.cs:35-36`).
2. Two *secondary* requests then answer **401**, and `sparkAuthInterceptor`
   (`ng-spark-auth/interceptors/src/spark-auth.interceptor.ts:14-28`) navigates to login on any 401:
   - `GET /spark/lookupref/DeleteBranchPolicy` — `Read/LookupReferences` is granted only to
     authenticated users (`security.json:165-166`). The Repository detail page needs this lookup for the
     `DeleteBranchOnPrClose` label.
   - `GET /api/browse/repos/...` (+ `/history`, `/branches`) — `SparkAuthorizeAttribute` derives from
     `AuthorizeAttribute` **without a policy**, so ASP.NET Core applies its default
     require-authenticated policy and answers `401 WWW-Authenticate: Bearer` *before* security.json is
     consulted. security.json's anonymous `Browse/Coverage` grant has therefore never had any effect.

So #453 as filed is a **usability bug** (anonymous visitors cannot stay on a public repository page),
not a data leak. Private repositories were verified to stop at `po/load` with 401 and no data, and
`BadgeToken` is blanked server-side for non-managers (`RepositoryActions.cs:184-188`; a dummy token set
on a local document never appeared in any response).

## 2. The requirement the investigation surfaced

Organisations install the GitHub App on private repositories. **The existence of a private repository
is itself private.** "This repository does not exist" and "this repository exists, but you may not see
it" must be exposed identically — status, headers, body, and without a usable timing difference — on
every repository-keyed surface, for anonymous callers and for authenticated non-members.

### 2.1 Measured state (anonymous, local host, 5 repeats each, byte-diffed)

| Surface | private vs missing |
|---|---|
| `po/load`, `queries/execute` (parentId), `queries/distinct`, `actions/execute` | identical (401 `{"error":"Authentication required"}` / 404 `Parent not found`) |
| `/api/browse/**`, `/api/repos/**/settings/*` | identical — but only because *everything*, public included, is a 401 before lookup |
| `/badge/{forge}/{owner}/{name}.svg` | bytes, ETag and Cache-Control identical — **timing is not: 4 ms vs 272 ms** |

### 2.2 The timing oracle (confirmed)

`RepositoryResolver.ResolveAsync` (`CodeCoverage.GithubIntegration/Services/RepositoryResolver.cs`):
step 1 finds a live name in RavenDB, step 2 an alias, step 3 asks GitHub — gated on the owner being a
known Account. The comment at 77-89 names the oracle and believes the account gate closes it. It does
not: the gate only stops *unknown* owners, and the organisations we must protect are exactly the known
ones. A guessed name under an App-installing org answers in ~4 ms if the private repository exists and
~270 ms (a GitHub round trip) if it does not. Badge, Browse, RepoSettings and uploads all resolve through
it; the badge is anonymous and needs no sign-in at all.

Secondary: `BrowseController.ResolveVisibleRepository` (`:584-593`) fetches the viewer's owners (a
possibly cold GitHub installation lookup) **only when the resolved repository is private**, so an
authenticated non-member sees the same kind of gap.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| D1 | `[SparkAuthorize]` names its own policy whose requirement always succeeds, so **security.json alone decides**, anonymous included. A refused anonymous caller is still challenged. | Fixes the Browse 401 at the root. Every other `[SparkAuthorize]` use is either not granted to anonymous (RepoSettings, Upload) or carries a separate `[Authorize]` (MeController) — verified. |
| D2 | `lookupref/{name}` is also allowed when the caller may Read an entity type that has an attribute bound to that lookup. The detail page no longer fails over a lookup. | A PO the caller may read must be renderable. Granting anonymous `Read/LookupReferences` wholesale would open every future dynamic lookup too. |
| D3 | **Invisible ≡ missing inside the resolver.** `ResolveAsync` takes the caller's visibility; a row the caller cannot see is treated as not found *at the step it was found* and resolution continues exactly as for a miss (alias query, account gate, GitHub lookup, cache). | Makes the private path and the missing path run the same work, so timing converges by construction instead of by padding. Uploads/settings pass their own authorization and keep today's behaviour. |
| D4 | The caller's owner set is fetched **before** resolving, unconditionally, for authenticated callers. | Removes the private-only cold GitHub call. |
| D5 | One canonical refusal on JSON surfaces keyed by a repository: `SparkDenial` — anonymous → 401 `{"error":"Authentication required"}` (no `WWW-Authenticate`), authenticated → 404 `{"error":"Not found"}`. Browse switches its "not visible"/"unknown forge" arms to it. Badge keeps its never-404 200 "unknown"; uploads keep their constant 404. | Same shape as `po/load`, so the SPA's sign-in prompt behaves the same on every surface. |
| D6 | Parity is enforced by tests that diff private-vs-missing responses (status, body, relevant headers) and assert the forge lookup is taken on both paths. | A clean status table today was only true because everything 401'd; it must be *held*. |

## 4. Out of scope (genuinely not done)

- **Account listing discloses the installation.** `Account` is anonymously QueryRead with no row
  filter, so an org whose only covered repositories are private is still listed. This reveals that
  the App is installed, not which repository exists. Hiding such accounts changes public product
  behaviour (account pages, search) and is a product decision for the owner — raised in the PR, not
  silently decided.
- Sub-millisecond differences in RavenDB query cost (measured < 1 ms on `po/load`) — not practically
  exploitable over the network.

## 5. Acceptance

1. Anonymous visitor on a public repository page stays on the page; no 401 is issued by any request it makes.
2. Anonymous and authenticated-non-member requests for a private repository and for a missing one are
   byte-identical on every surface in §2.1, by id and by name (live name, alias, made-up name under a
   known owner, unknown owner, unknown forge).
3. Private and missing names under a known owner take the same resolver path (both reach the forge
   lookup when applicable), verified by test.
4. `/api/me/**`, RepoSettings and uploads still refuse anonymous callers.
5. All five test projects green; `--spark-verify-model` clean; `libs/` packages bumped within their
   current major.
