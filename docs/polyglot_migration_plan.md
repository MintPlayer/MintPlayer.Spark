# Plan — Polyglot migration

**PRD:** [polyglot_migration_PRD.md](polyglot_migration_PRD.md)
**Branch:** `feat/polyglot-migration` off `master` (`e39b16de`). It holds docs only so far.
**One PR** on the Spark side: the GS1 library, the TS twin, CI and docs land together, after the upstream milestones.

## Status

| Milestone | State |
|---|---|
| U0: v0.10.1 probe; [Polyglot#73](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73) and [#74](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/74) filed | ✅ 2026-10-17 |
| U1: P40 decisions accepted with Spark's terms; acceptance checklist posted | ✅ 2026-10-17 ([6233584472](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6233584472), [6233861572](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6233861572)) |
| U2: review rounds on PR [#75](https://github.com/MintPlayer/MintPlayer.Polyglot/pull/75) | ✅ ten rounds; approved `629b0c2` ([6258506901](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6258506901)) with two non-gating items, fixed in `3e9a2f5` |
| U3: **approval comment** on #73 | ✅ 2026-10-19: approval 6258506901, confirmation of `3e9a2f5` [6262383457](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6262383457) |
| U4: a Polyglot release carrying #73 (`MintPlayer.Polyglot.MSBuild`) | ✅ 0.10.2, 2026-10-19 (GitHub Packages 08:12Z, nuget.org ~08:24Z). `nuget.config` maps `MintPlayer.*` to the GitHub feed (`GH_PKG_USER`/`GH_PKG_TOKEN`; CI passes `github.actor` and `GITHUB_TOKEN`) |
| S1: `libs/gs1` project with the package, `PolyglotTargets=csharp`, internal access, its own namespace, the facade | ✅ `libs/gs1/MintPlayer.Spark.Gs1` (namespace `MintPlayer.Spark.Gs1.Core`, class `Gs1Core`, internal); facades `Gs1CheckDigit`, `Gs1Barcode`, `Gs1Sscc` |
| S2: TS twin routed into ng-spark `scanning`, `.gitattributes` `eol=lf`, CI `polyglot build --check --only typescript` from the config dir, barrel outside the owned folder | ✅ new entry point `@mintplayer/ng-spark/scanning` (22.42.0); owned folder `scanning/src/gs1/`; CI step in `pull-request.yml` with the package's linux-x64 CLI |
| S3: GS1 core in `.pg` (check digits, SSCC, element strings, Digital Link, AI linters), with the AI tables from the `tools/` script (Q-W16) and Syntax Engine vectors run against both outputs | ✅ `tools/gs1/generate-ai-table.mjs` emits the vendored Syntax Dictionary as `pg/aitable.pg` (one table, transpiled to both targets); `pg/` holds element strings (symbology identifiers, GS/FNC1, bracketed, `ex=` pairs, repeats), Digital Link, SSCC and all 39 linters (5683/5683 GS1 unit vectors on C# and TS). **W12:** `libs/gs1/corpus/gs1-corpus.json`, 2995 cases from `tools/gs1/build-corpus.mjs` (Syntax Engine 1.4.1 WASM, RequisiteAIs off), every dictionary AI covered; `tests/MintPlayer.Spark.Gs1.Tests` and `scanning/src/gs1-corpus.spec.ts` agree on all of them |
| S4: consumers (commerce GTIN validation; ng-spark scanning) | ⏳ ng-spark `scanning` exports the parser; commerce GTIN validation waits for the commerce code (#521, branch `feat/issue521-commerce-suite`) |
| Fallback | If no Polyglot release with #73 ships before warehouse WM2, hand-write both parsers against the Syntax Engine vectors (owner decision, 2026-10-17) |

## Review history of PR #75

| Head | Date | Result | Comments |
|---|---|---|---|
| `612e2fe` (PR out of draft: 14 slices, gate 989/0) | 2026-10-17 | changes requested | [part 1](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6239298760), [part 2](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6239738373) |
| `281314c` | 2026-10-18 | round 3: 7 gating | [part 1](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6242657697), [part 2](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6242658042) |
| `f00dd24` | 2026-10-18 | round 4: 22 gating; bar narrowed | [part 1](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6244347379), [part 2](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6244347721) |
| `c436aae` | 2026-10-18 | round 5: 24 gating (8 R, 9 H, 7 O); bar narrowed to silent miscompiles | [part 1](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6245914322), [part 2](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73#issuecomment-6245915312) |

**What each round found:**
- **`612e2fe`:**
  - D17: deleting a `.pg` left a stale DLL, because the transpile ran after `_GenerateCompileDependencyCache`;
  - deletion-invariant holes: a substring marker, the map sidecar, an unbounded owned scope, a leading `/`, overwrite;
  - TS2871 from `Map.get(k) ?? d` on TS ≥5.6;
  - a case-only rename on Windows made Linux CI exit 3 forever (A3);
  - a partly corrupt record still deleted, and a broken pgconfig was ignored;
  - receiver parenthesization, lambda `T?`, a D16 overload leak, record custom-eq hashing, definite assignment.
- **`281314c`:**
  - non-ASCII project paths (`--outputs-list` was written in ANSI);
  - silent miscompiles: `adder(10)(4)`, `-(-x)` → `--x`, colliding compound-assignment temps;
  - A1 chains still gave TS2871, plus A3 with maps, git and non-ASCII names, and A8 on C#;
  - `check` disagreed with `build`, C# compile errors on accepted programs, and `PolyglotEntryPoint` incrementality.
- **`f00dd24`:**
  - the stale-temp sweep deleted header-less files, and A3 missed directory components;
  - base constructors were dropped on PHP and Python;
  - nullable → non-null was unchecked, PHP read operands late, and C# link names collided;
  - emission order and record members;
  - `??=`, unit lambdas, PHP function values, a TS union-case lambda, and builtin name collisions.
- **`c436aae`:** all 22 round-4 repro sets pass and the regression set is clean. The remaining 24 items are in the next section.

**Where `c436aae` stands, verified:**
- every one of the 146 older corpus programs is identical between `f00dd24` and `c436aae`;
- `c436aae` passes all 158 corpus variants at the C# floor, with tsc strict and the ng-spark flags;
- Polyglot's own conformance runner gives 169/169, the library gate is green over 166 programs, and the floor gate's canary now fails with TWAE off;
- Spark's D3 workflow works with the packaged exe, and D17 and incrementality hold, including `PolyglotEntryPoint` and `PolyglotOutDir` flips.

## Round 5 gating items, and how to re-verify each one

Repro paths are relative to the session scratchpad, `%TEMP%\claude\C--Repos-MintPlayer-Spark\73183653-24bf-47a7-b375-6516bae73e6d\scratchpad\`. Tags: [R] regression against `f00dd24`, [H] hole in a round-4 fix, [O] older than #75.

| # | Item | Repro | Pass when |
|---|---|---|---|
| 1 | [H] A3 misses directory components of a route that leaves the config folder (Spark's layout) | `pgc43_h1\a3x.sh`, `a3x2.sh`, `a3m.sh` (copy: `pgc43_me\a3x.sh`) | local `--check` exits 3 with `would rename` (or a refusal); the build renames or refuses; a case-sensitive clone's `--check` exits 0 after the hint; the template-case change too |
| 2 | [H] A directory created earlier in the same run is missing from the cached listing (rules `gen/` + `Gen/`) | `pgc43_r1\h1.sh` (copy: `pgc43_me\r1\h1.sh`) | the first build refuses naming both spellings, or writes one consistent spelling; a case-sensitive copy's `--check` exits 0 |
| 3 | [R] Include-rule target from an uncached registry dependency, not in `targets`: offline exit 64 | `pgc43_r1\rt.sh` config 2 (copy: `pgc43_me\r1\rt.sh`) | offline `check` exits 0 with "not verified" |
| 4 | [R] A module value reaching a later class through a constructor, instance method or extension crashes TS and Python at import | `pgc43_r2\src\o1`, `o2`, `o13`, `o15`, `tc6`; `pgc43_h2\src\x7p`, `x7q` | refused like `o3`, or all four print the right value |
| 5 | [H] The constructor virtual-call check misses lambdas and `this` escaping | `pgc43_r2\src\c5d`, `c5e`, `c5h`; `pgc43_h2\src\x5c`, `x5d`, `x5e`, `x5k`, `x5i` | refused, or all four agree |
| 6 | [R] A class nothing can derive from is refused for calling its own override | `pgc43_r2\src\c5a`, `c5b` | builds; `button:ok|ok` and `base` on all four |
| 7 | [O] C# runs subclass field initializers before the base constructor (silent) | `pgc43_h2\src\x3h`, `x3i` | all four agree (`1|2`, `BcC`), or refused |
| 8 | [O] Statements before `super(…)` are reordered | `pgc43_r2\src\c4b`; `pgc43_h2\src\x4c`, `x4b`, `x4e` | refused, or compiles and agrees |
| 9 | [O] `abstract fn` is emitted as an empty concrete method | `pgc43_h2\src\x3e2`, `x3e` | C# floor and tsc clean; all four agree |
| 10 | [H] `open static` / `override static` | `pgc43_h2\src\x6j`, `x6k` | refused |
| 11 | [H] Record positional defaults are dropped (item 8's own remedy) | `pgc43_r2\src\o5b`; `pgc43_h2\src\x8e` | compiles and agrees, or refused with the remedy text changed |
| 12 | [O] TS static initializer calling a free function that reads an earlier static | `pgc43_r2\src\tc5` | `/api/users` on all four, or refused |
| 13 | [R/H] C# link names decided by call sites, not declarations | `pgc43_r2\src\ln4` (+`ln4a`/`ln4b`), `ln3` (+`ln3a`/`ln3b`) | C# compiles; no PHP redeclare (or refused) |
| 14 | [H] A function used as a value keeps its provisional link name | `pgc43_r2\src\fv3` (+`fv3m`), `fv3b` (+`fv3bm`) | C# compiles, PHP runs, all four agree |
| 15 | [H] A union-typed parameter accepts any argument; overloads mis-bound | `pgc43_r2\src\ov1`, `ov2`, `ov6` | `ov1`/`ov2` bind the generic and `Animal` overloads and compile; `ov6` refused |
| 16 | [R/H] Runtime names: TS over-refuses `Symbol`/`Object`; holes for Python `id`, `class Error`, `let Error`, PHP `intdiv`/`strpos`/`fmod` | `pgc43_r2\src\rn1`, `rn3b`; `pgc43_h3\src\k22id`, `k22pi`; `pgc43_h3\rt\src\rt_class_Error` (net9) | `rn1` builds on TS; the rest agree or are refused by name |
| 17 | [R] Unused locals: `_last` refused with a false claim; elided body → empty Python `def` | `pgc43_r2\src\u1`, `u2` | `u1` builds and agrees; `u2` runs on Python |
| 18 | [H] Nullable values still reach non-null slots (match arms, `yield`, `with`, index args, tuples, generic args, lambda params, generic `T`) | `pgc43_h3\src\k14m`, `k14m2`, `k14y`, `k14w`, `k14i`, `k14t`, `k14gv`, `k14lp`, `k14lq`, `k14gen`, `ktype`, `klam` | refused |
| 19 | [R/H] A captured variable narrowed inside the lambda body is refused | `pgc43_h3\src\k14nc2`, `k15cin2` | builds and agrees (`in|none`; `captured-inside|done`) |
| 20 | [O] `?.`: `r?.run()` loses its `?.`; on TS `b?.n` is `undefined` (silent) | `pgc43_h3\src\k18q2`, `knull` | C# and tsc clean; `?.` emitted; `knull` prints `true|true|none` on all four |
| 21 | [O] A lambda as `??`'s right operand loses its parentheses | `pgc43_r2\src\nf6` | compiles; `9|4` on all four |
| 22 | [O] `apply<T>(f: () => T)` with a unit lambda: C# CS0411 | `pgc43_h3\src\k18g` | C# compiles, or refused |
| 23 | [H] PHP: a plain `=` doesn't snapshot its target's index or receiver | `pgc43_h2\src\x9c`, `x9d` | PHP equals C#/TS |
| 24 | [R] C#: the statement discard `_ = ` binds to a user variable named `_` (silent) | `pgc43_h4\corpus\src\zz_let_discard`, `zz_underscore_bool`, `zz_underscore_param`, `zz_underscore_lambda` (net9) | C# compiles; `false|0` and friends on all four |

**Non-gating (round 5 part 2), to be fixed or documented in the same PR:**
- **CLI:**
  - a two-level rename prints one git hint per level, and the later hints exit 128;
  - a rule target naming a loaded but unconfigured target is accepted; `[]` acts as all targets;
  - docs: offline "not verified", the sweep during a failing run, and map temps swept on their `file` field;
  - pin the floor total at the corpus size (157), not ≥146.
- **Language:**
  - the field-narrowing refusal should name its rule;
  - document exact function-type matching, or support variance;
  - document that generic and defaulted functions are refused as values;
  - the through-call check doesn't follow extensions or functions passed as values;
  - PHP arrow functions capture module values early;
  - `h?.f(5)` is refused;
  - a bare function name used as a statement;
  - Python late reads in a plain `=`;
  - PHP union `==` and `+` concatenation;
  - module-level lambda values can't be called;
  - the f32 overflow message;
  - CS8981 for all-lowercase class names;
  - two hint nits.

## How to resume after a Polyglot push

1. **Watcher.** `pg73_watch2.sh <known_ids> <known_xrefs> <event> "false open false" <head_sha>` runs in the background. It exits on `NEW_COMMENTS`, `PR75_HEAD_MOVED`, `PR75_STATE_CHANGED`, `ISSUE_CLOSED`, `NEW_CROSS_REFERENCE`, `WATCH_FAILING` or `WATCH_TIMEOUT`, and writes the reason to `pg73_event.txt`.
   - Never edit the script while it runs.
   - Stop it before posting.
   - Append every comment id, including your own, to `pg73_known_ids.txt`, then re-arm.
2. **On a new comment:** fetch it (`gh api repos/MintPlayer/MintPlayer.Polyglot/issues/comments/<id>`), answer only if it needs an answer, then re-arm.
3. **On a push (the head SHA posted in #73):**
   - copy `pgc43_build.sh` with the new SHA, version `0.11.0-pr73.<sha7>` and new folders (`p5/`, `pg75feed5/`, `pg75_cli5/`), and run it in the background;
   - post a short ack and re-arm the watcher;
   - write the agents' rules file from `pgc43_rules.md`, repointed;
   - delegate four jobs: the CLI items (1–3), language items 4–12, language items 13–24, and the regression sweep (`pgc43_h4`: corpus diff, hA/hD/hB, the 77 probes, the floor), plus a delta review limited to the fix commits.
4. **Personal re-run** of every gating finding with `pgc43_me\lang\t.sh` / `batch.sh` (add the new CLI to `t.sh`), summarized with `sum.awk`. Programs that use `Map` need `TFM=net9.0`.
5. **Apply the bar** (PRD §6), then post the approval, or the remaining gaps in two parts. Update this plan, the status file `pgc43_status.md` and the project memory.

**Scratchpad index:**
- the round-6 table: `pg73_reverify_plan_r6.md`;
- status: `pgc43_status.md` and the per-agent `pgc43_status_<tag>.md`;
- the posted comments: `pg73_review_round5.md`, `_p2.md` and the earlier rounds;
- the Polyglot replies: `pg73_c<id>.md`;
- builds: `p4/` (source), `pg75_cli4/` (exe and nupkg copy) and `pg75feed4/` for `c436aae`; `pg75_cli3`/`pg75feed3` for `f00dd24`; `pg75_cli2` for `281314c`; `pg75_cli` for `612e2fe`.
