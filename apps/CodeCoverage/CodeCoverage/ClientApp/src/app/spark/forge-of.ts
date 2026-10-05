/**
 * The canonical forge spelling (`github`, `gitlab`, `bitbucket`) a forge-qualified document id
 * carries in its second segment — `Repositories/github/123`, `Commits/github/123/{sha}`,
 * `Accounts/gitlab/7` — or `null` when the id carries none.
 *
 * ⚠️ This is where the client gets the forge from since #264. It used to read the `OwnerKey`
 * attribute (`github:MintPlayer`) off the row or object, which kept a server-side key on the wire
 * for every viewer only so that its prefix could be split off. The id already says it: every
 * document of a forge-scoped type has been keyed by forge since the forge-qualified-ids migration,
 * and the id ships on every row and object whatever the caller may read.
 *
 * Accepts a document id string, or anything with an `id` (a query row or a persistent object).
 */
export function forgeOf(source: unknown): string | null {
  const id = typeof source === 'string' ? source : (source as { id?: unknown } | null | undefined)?.id;
  if (typeof id !== 'string') return null;
  const segment = id.split('/')[1];
  // Lowercase letters only: the canonical spellings are, and anything else (a numeric legacy id
  // segment, an empty string) is not a forge and must not become a route.
  return segment && /^[a-z]+$/.test(segment) ? segment : null;
}
