# format

The runner side of CodeCoverage's formatting (#657, [PRD](../../../docs/coverage_file_formatter_PRD.md)). The
workflow that calls it is [`apps/CodeCoverage/CodeCoverage/Formatting/formatting.yml`](../../../apps/CodeCoverage/CodeCoverage/Formatting/formatting.yml),
which onboarding's Formatting checkbox installs as `.github/workflows/formatting.yml`.
CodeCoverage dispatches that workflow and pushes the patch the run uploads; the run itself never commits or pushes.

## Modes

| Mode | Started by | Tools |
|---|---|---|
| `format` | the FormatRepository action | dotnet format whitespace and style, Prettier, gofmt, rustfmt, ruff format or black, terraform fmt |
| `lint` | the LintRepository action | dotnet format analyzers, ESLint `--fix`, ruff check `--fix`, clippy `--fix` |
| `pipeline` | the merge queue and backports, for a `whitespace` rule | the format tools, dotnet whitespace only |

A tool runs only on the files with its extensions, passed as explicit lists of up to 400 (never `.`: `ruff format .`
reaches Markdown, S8). `exclude` is never touched, and the pipeline never touches `.github/**`. After the run, every
changed file outside those lists is restored, so a tool that reaches further (clippy, dotnet style) changes nothing else.

## Versions

Every tool runs at the version the repository pins, never at the newest (S8):

- **.NET:** `global.json` through `actions/setup-dotnet`; without one, SDK 10 (whitespace is the same on 10 and 11, S1).
  Style and analyzers need exactly one solution at the root.
- **JS:** the root `package.json`'s `prettier`/`eslint`, installed through its own lockfile and package manager with
  scripts off (`npm ci --ignore-scripts`, `pnpm install --frozen-lockfile --ignore-scripts`,
  `yarn install --immutable --mode=skip-build`), then `npx --no-install` (S2). ESLint runs in the directory of each
  file's nearest `eslint.config.*`.
- **Go:** `go.mod`. **Rust:** `rust-toolchain(.toml)`, and the edition from the nearest `Cargo.toml`.
- **Python:** ruff when `[tool.ruff]` or `ruff.toml` exists, else black when `[tool.black]` does; the version from
  `uv.lock`, `poetry.lock`, `requirements*.txt` or `.pre-commit-config.yaml`.
- **Terraform:** `.terraform-version`.

The report (`format-report` artifact, format and lint) names every tool and the version it ran at; CodeCoverage puts
them in the pull request's body.

## The pipeline's proof (D17)

The merge queue resolves a conflict under a `whitespace` rule with `git merge-file --theirs`, the pull request's side.
This run proves that was safe:

1. `fetch-commits.sh` fetches the base branch's side (`ours_sha`), the pull request's (`theirs_sha`) and their merge
   base. It is the only step that holds a token, and it runs before any of the repository's code.
2. Candidates are the files changed on both sides whose three-way merge conflicts, whose `--ours` and `--theirs`
   resolutions differ, and whose prepared copy equals the `--theirs` resolution (S10: byte-identical to the writer's
   on git 2.43–2.54; `--diff-algorithm` only on git ≥ 2.44).
3. The run fails if a candidate is under `.github/**` or `exclude`, if no formatter covers it, or if the pull request
   changes what the formatter reads (`.editorconfig`, `global.json`, a Prettier config, a lockfile…, D9.3).
4. Both resolutions are formatted. They must be byte-identical; any difference fails the run, and the queue evicts.
   Spacing passes and whitespace inside a string fails with every formatter (S11).
5. If the base branch's copy is not formatted itself, the proven resolution is kept unformatted, with a note (D16).

## Outputs

- `merge-queue-patch`: `merge-queue.patch`, `git diff HEAD` of the changed files (possibly empty).
- `format-report` (format and lint): `format-report.json` with the tools, their versions, the notes, the files and,
  for lint, the problems the linters could not fix.

## Tests

`node --test .github/actions/format/format.test.mjs` (part of `npm run test:tools`).
