/**
 * Compile-time completeness check for a hand-written mirror of a generated contract
 * (docs/guide-typescript-contracts.md).
 *
 * `PersistentObject` and `PersistentObjectAttribute` stay hand-written because the client relies on narrower
 * member types than the wire states (`id: string` on a loaded object, `isArray?` on a client-built attribute).
 * What a mirror must not do is lose a member the server sends, the drift that left `options` out of the
 * attribute: `MissingMembers` names every such member, and `AssertNone` only accepts `never`, so a member added
 * to the C# type fails the library's build until the mirror carries it.
 */
export type MissingMembers<TContract, TMirror> = Exclude<keyof TContract, keyof TMirror>;

/** Accepts only `never`; see {@link MissingMembers}. */
export type AssertNone<T extends never> = T;
