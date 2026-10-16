# GitHub answers for the onboarding admin check (dependency-updates PRD R21, R33, R35; captured 2026-10-16)

REAL, captured with `gh` (the owner's **user** token, never printed), scrubbed like `../DependencyUpdates/SOURCE.md`.

- `graphql-collaborators-partial.json`: the aliased query `GitHubRepositoryAdministration` sends, for two repositories,
  `r0` the owner's scratch repository and `r1` `torvalds/linux`. GraphQL answers **per alias**: `r0` has its edge
  `permission: ADMIN`; `r1` is `collaborators: null` with an error `FORBIDDEN` whose `path` is `["r1","collaborators"]`.
  So one refused repository does not fail the others, and the adapter falls back to REST for the refused aliases only.
- `rest-collaborator-permission-admin.json`: `GET /repos/{o}/{r}/collaborators/{login}/permission` for the repository's
  admin: `permission: admin`, `role_name: admin`.

⚠️ **Not verified: the same query with an App installation token.** No installation token can be minted from this
machine. Whether an installation token (Metadata: read) may read `collaborators` is exactly what the automatic REST
fallback covers: any refused alias, or an error naming no alias, is asked again over REST.
