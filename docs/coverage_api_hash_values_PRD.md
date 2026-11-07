# A constant's value is not API (#576) — PRD

Issue: [#576](https://github.com/MintPlayer/MintPlayer.Spark/issues/576). Plan: [coverage_api_hash_values_plan.md](coverage_api_hash_values_plan.md).
Amends [coverage_package_versioning_PRD.md](coverage_package_versioning_PRD.md) D6 and plan S3.

## 1. What happened

The versioning engine released `@mintplayer/ui-core` **1.8.0** from 1.7.0 (mintplayer-ng-bootstrap run
42207347316, commit `6ad1d95c8`). The only commit since the 1.7.0 tag was a `fix:` that changed three `.scoped.scss`
files. A patch, 1.7.1, was expected.

The owner's rule (2026-11-07): "the version bump should always only happen when the public API of a package changes,
or the developer applies a git tag himself." The engine already decides that way: changed inputs only decide *whether*
a package releases, and the API hash alone decides minor vs patch (`VersionCalculator.cs:61-64`; commit types are not
read, PRD line 58). The fault was in what the hash measured.

## 2. Cause (evidence)

| Step | Evidence |
|---|---|
| ui-core compiles each stylesheet into an exported constant | `libs/mintplayer-ui-core/tooltip/src/styles/tooltip.scoped.styles.ts:5`: ``export const tooltipScopedStyles = `@layer mp.bootstrap…` ``, re-exported from `tooltip/src/index.ts:4` |
| The compiler writes the whole CSS into the declaration | built `dist/libs/mintplayer-ui-core/tooltip/src/styles/tooltip.scoped.styles.d.ts`: `export declare const tooltipScopedStyles = "@layer mp.bootstrap, mp.components;\n…";` |
| The listing kept string literals verbatim | `TypeScriptApiListing.cs` (before): comments, whitespace and private members out, literals in |
| The two tag hashes differ | tag `@mintplayer/ui-core/v1.7.0` (object a622c3e3): `sha256:6ca760c3…555b`; `v1.8.0` (object 2014d8bd): `sha256:9fbed120…161d`; both `engine: CodeCoverage.Versioning 1.0.0`; no registry fallback ran (no note in the run log) |
| Reproduced from the published packages | S2 below: listing 1 over the npm tarballs gives exactly those two hashes |

So a CSS change was an API change, and `apiChanged` gave a minor.

## 3. Decisions

### D1 — A literal initializer lists as its type

The TypeScript compiler writes an initializer into a declaration only where it inferred a literal type (S1): a `const`
(`declare const a = 1, b = "two";`) and a `readonly` class property (`static readonly tag = "x";`), with a string,
number, bigint or boolean. The listing replaces each such initializer with its type
(`export declare const styles: string;`).

- **Why:** the value of a constant is not its contract. A stylesheet, a version string or a default number changes
  its value without any consumer's code having to change. Changing the *type* (string to number, removing the
  constant) still changes the hash.
- **Stays as API:** a literal type the author wrote (`const a: "x"`), `type` aliases (`type Mode = "light" | "dark"`)
  and `enum` members (`A = "a"`). Those are declared contracts: a consumer's code depends on the exact value.
- **Not covered, deliberately:** a `declare const` statement whose type contains `;` before a later literal declarator
  (`declare const o: { a: number; }, b = 1;`) keeps `b`'s value. The compiler never emits that shape for a single
  declarator, and over-bumping a minor is the harmless direction (D6).
- **Rejected alternative:** have ng-bootstrap annotate its generated constants `: string`. It fixes one producer and
  leaves the trap for every other package, and the owner's rule is about the engine's behaviour. It remains a fine
  change for ng-bootstrap on its own, but it is not needed.

### D2 — The hash names the listing that produced it

Changing the listing changes the hash of an unchanged API. Without care, the first release of every npm package
after this fix would compare a listing-1 tag hash against a listing-2 build hash and bump a minor once more, which
is exactly the bug.

- **Form:** listing 1 stays `sha256:<64 hex>` (every existing tag, and every .NET hash, unchanged); a later listing is
  `v<N>:sha256:<64 hex>` (`ApiHash.Of`, `Identifiers.IsApiHash`). TypeScript is now listing 2; .NET stays 1.
- **Compare only like with like:** the action (`BuildManifest`) asks the registry for the last release's hash when the
  tag's hash is absent **or made by another listing**. The registry path hashes the published tarball with the
  current listing. The planner uses the tag's hash only when its listing matches the build's, and otherwise the
  registry's rehash; if neither exists, the API counts as changed (D6, unchanged).
- **Why in the hash and not in a separate annotation line:** the hash then travels unchanged through the manifest,
  the server, the GitWriter tag wire and the annotation, with no new field on any of them. And it stays right when
  the action and the server run different engine versions, which is the normal case: mintplayer-ng-bootstrap pins
  the action to a commit SHA (`publish-master.yml:82`, `versioning-v1.1.0`), while the server deploys from master. An
  old action sends listing-1 hashes, which are compared with listing-1 tags exactly as before. A separate
  `api-listing:` line written by the server would describe the server's listing, not the one that made the hash.
- **Rollout order:** the server must accept `v2:` hashes before an action that sends them runs. The server deploys
  from master when this merges, and a repository picks up the new action only by moving its pin, so the order holds.

## 4. Spikes

- **S1 — what the compiler emits.** TypeScript 6.0.3, `tsc --declaration` over a sample of every const and readonly
  form. Initializers are emitted for string (`"it's \"q\""`, single quotes normalized to double), number (`-1500` from
  `-1.5e3`, `255` from `0xff`), bigint (`10n`) and boolean; for multi-declarator lists (`a = 1, b = "two"`); and for
  `static readonly` and `readonly` class properties. Not for `: string` annotations, objects, or `as const` (which
  emits the type `: "x"`). Covered by `TypeScriptApiListingTests.Each_literal_the_compiler_emits_becomes_its_type`.
- **S2 — the real packages.** `npm pack @mintplayer/ui-core@1.7.0` and `@1.8.0` (839 declaration files each), hashed
  with the old listing and the new one:

  | Version | Listing 1 (before) | Listing 2 (after) |
  |---|---|---|
  | 1.7.0 | `sha256:6ca760c3…9555b` (= the tag) | `v2:sha256:0e08bf0a…6b2588` |
  | 1.8.0 | `sha256:9fbed120…b161d` (= the tag) | `v2:sha256:0e08bf0a…6b2588` |

  Listing 1 reproduces both tags exactly, so the spike models the engine. Listing 2 gives equal hashes: this release
  would have been 1.7.1.

## 5. Consequences

- The next release of an npm package whose last tag carries a listing-1 hash asks the registry once. For a package
  that is not on the public registry, that one release counts its API as changed (D6's existing fallback), and every
  later release compares listing 2 with listing 2.
- A repository gets the fix when it moves its action pin to a release that includes this change. Until then nothing
  changes for it.
- A future change to either listing must raise that listing's `Version` constant. The constant's doc comment says so.
