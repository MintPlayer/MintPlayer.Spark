# GitHub answers for "who manages an organization" (dependency-updates PRD D-O12, owner decision R39; captured 2026-10-16)

REAL, captured with `gh api -i` (the owner's **user** token, never printed; the raw capture had no `gh[pousr]_` string),
then scrubbed:

- `membership-admin-active.json`: `GET /user/memberships/orgs/MintPlayer` for an owner of that organization: `200`,
  `role: admin`, `state: active`, `direct_membership: true`. Scrubbed by plain text replacement: the user's login to
  `acme-owner`, the organization's login to `acme`, the user's id to `1001` and the organization's id to `5` (with the
  matching `node_id`s re-encoded), and the organization's description emptied. Nothing else changed; the headers were
  dropped.
- `membership-not-a-member-404.json`: the same request for an organization the user is not a member of (`nodejs`):
  `404`, `{"message":"Not Found", ...}`. Unscrubbed: it names nobody.

Derived, not captured: `GitHubOrganizationOwnerTests` turns the owner's body into a `member`, a `billing_manager` and a
`pending` one by changing that one field, as GitHub documents those values for this endpoint. A read-only member's real
answer could not be captured from this account (it owns every organization it belongs to).

⚠️ The App's user token needs the **Members: read** organization permission for this endpoint; the App declares it
(README, "Members: Read-only"). An organization that has not granted it answers `403`, which the app reads as "not an
owner" (fail closed).
