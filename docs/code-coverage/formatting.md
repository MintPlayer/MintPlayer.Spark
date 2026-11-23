# Formatting

**#657.** CodeCoverage formats or lints a repository with **the repository's own formatters, at the versions it pins**,
and opens the result as a pull request. The merge queue and backports use the same workflow to resolve conflicts that
differ only in whitespace.

The formatter **never runs on the server**. It runs in your repository's GitHub Actions runner, through the
`formatting.yml` workflow, and uploads a patch. CodeCoverage checks the patch and pushes it itself; the workflow never
commits or pushes.

Design and decision log: [`../coverage_file_formatter_PRD.md`](../coverage_file_formatter_PRD.md) (D1–D21). Plan:
[`../coverage_file_formatter_plan.md`](../coverage_file_formatter_plan.md).

## Setting it up

Tick **Formatting** in **Set up features** ([onboarding.md](onboarding.md)). The onboarding pull request adds
`.github/workflows/formatting.yml`, which calls the composite action `MintPlayer/MintPlayer.Spark/.github/actions/format`
at the moving tag `format-v1`.

A repository without `formatting.yml` on its default branch is refused: "set up Formatting first" (D1). There is no
server-side fallback.

**Run FormatRepository once, right after onboarding** (D4). The pipeline step never formats a file that was not already
formatted (D16), so a repository that was never formatted gets no whitespace conflicts resolved.

### What the workflow runs (D13)

| Ecosystem | `format` | `lint` |
|---|---|---|
| .NET | `dotnet format whitespace` + `style`, per solution, with `setup-dotnet` from `global.json` | `dotnet format analyzers` |
| JavaScript/TypeScript | Prettier | `eslint --fix`, per nearest `eslint.config.*` |
| Python | `ruff format` or `black`, whichever `pyproject.toml` configures | `ruff check --fix` |
| Go | `gofmt` | format only |
| Rust | `rustfmt` | `cargo clippy --fix` |
| Terraform | `terraform fmt` | format only |

- Each tool runs at the version the repository pins: the lockfile for JS (installed with scripts off), `uv.lock`,
  `requirements*.txt` or pre-commit for Python, `rust-toolchain`, `go.mod`, `.terraform-version`.
- Tools get explicit file lists, never `.`, of at most 400 files each.
- The pull request body names every tool and version used, and the lint problems left that the linters could not fix.

### `exclude` (D11)

Paths the formatter must never touch are written in the workflow's `with:`, not in `coverage.yml`:

```yaml
      - uses: MintPlayer/MintPlayer.Spark/.github/actions/format@format-v1
        with:
          exclude: |
            vendor/
            *.generated.cs
            ./src/Legacy/**
```

- A glob without `/` matches at any depth (`*.generated.cs` is `**/*.generated.cs`).
- A trailing `/` means the whole directory (`vendor/` is `vendor/**`).
- A leading `./` is dropped.

The runner skips excluded files. The server reads `exclude` from the default branch's copy of `formatting.yml` and
refuses an on-demand patch that touches one anyway. In the pipeline mode an excluded conflicted file fails the run
(D19), so the pull request is evicted rather than formatted.

### The security header

The template starts with a SECURITY block. In the pipeline mode the workflow runs the repository's formatter configs,
which can be code, on a pull request's merge. So:

- never add a secret, an environment, or `${{ secrets.* }}`;
- never widen `permissions:` or add `id-token: write`;
- never run it on a self-hosted runner;
- keep `persist-credentials: false`.

The queue checks that the `mq/` branch's copy of `formatting.yml` equals the default branch's before every dispatch.

## FormatRepository and LintRepository

Two actions on the **Repositories** query (D3). Select one or more repositories you manage and run the action.

1. When the repository has the merge queue feature, the action asks: **Open the pull request**, or **Open it and add it
   to the merge queue** (D5). The queue still enforces approvals and checks.
2. CodeCoverage dispatches `formatting.yml` with `mode: format` or `mode: lint` on the default branch's head.
3. The run's patch is checked (the patch guard below), committed by the bot to `mintplayer/format` or `mintplayer/lint`
   (force-pushed, one branch per mode), and the pull request "Format the repository" or "Lint the repository" is opened
   or updated.
4. You get a notification (the bell) with the pull request link, or the outcome: already formatted, or failed with the
   reason ("the run finished `failure`", "its patch was refused (…)", a timeout).

The on-demand pull request may format `.github/**` (D7): it goes through the bot's pull request path with
`workflows: write`.

Each run is a `FormatRun` (one per repository and mode, D14). A new request replaces a pending one of the same mode, and a
run with no result after its timeout fails.

### The patch guard (D15)

Both profiles accept only modified regular files (mode 100644): no new file, no deletion, no rename, no binary patch.

- **On demand:** any writable path, `.github/**` included, minus `exclude`.
- **Pipeline:** never `.github/**`, and only the files resolved under the `whitespace` strategy (D18).

## The `whitespace` strategy

An `autoResolve` strategy for the merge queue ([merge-queue.md § 2.2](merge-queue.md#22-coverageyml--mergequeue)) and
backports ([backports.md](backports.md)). It resolves a conflict **only when both sides are equal once whitespace is
removed**, and then lets the runner prove it (D17).

1. **The writer** reads the diff3 regions (`git merge-file --diff3 --diff-algorithm=myers`). Every region's two sides
   must be equal without whitespace; an add/add conflict, a side that is not UTF-8 or a marker-like line refuses
   (`resolver-refused`). It resolves the file with the pull request's side (`merge-file --theirs`).
2. **The runner** (`formatting.yml`, `mode: pipeline`) rebuilds both whole-file resolutions, `--ours` and `--theirs`,
   from the three commits it is given, formats both with **the target branch's** formatter, and requires them to be
   byte-identical. Formatters never change string contents, so the formatter's own rules decide what counts as
   whitespace.
3. A failed proof fails the run, and the queue evicts the pull request as an unresolved conflict.
4. A proven file whose target copy is not formatted is kept as the writer made it, with a note (D16, D19).

A pull request that changes the formatter's inputs (its config, plugins or version) fails the run: the proof would run
the pull request's own config, so it would prove nothing (D19).

### Using it

The preset turns it on for the formatter-safe types:

```yaml
mergeQueue:
  autoResolveDefaults: [formatting]
```

`formatting` adds `whitespace` rules, regenerated by `formatting.yml`, for `**/*.cs`, `*.ts`, `*.tsx`, `*.mts`, `*.cts`,
`*.js`, `*.jsx`, `*.mjs`, `*.cjs`, `*.css`, `*.scss`, `*.less`, `*.go`, `*.rs` and `*.tf`. YAML, Python, Markdown and
other whitespace-significant types are not in it and keep today's behaviour.

An explicit rule works too:

```yaml
mergeQueue:
  autoResolve:
    - path: "src/**/*.cs"
      strategy: whitespace
```

A `whitespace` rule always regenerates with `formatting.yml`. `regenerate: false`, another `action`, or
`postRebaseWhen: never` is an error. Unlike other explicit rules, a `whitespace` rule never adds its glob to
`regeneratedPaths`: the patch may touch only the concrete files it resolved.

### In the merge queue

After the rebase, a whitespace resolution dispatches `formatting.yml` on the `mq/` branch with `mode: pipeline`,
`ours_sha` = the base it rebased onto and `theirs_sha` = the head it merged. The runner computes the merge base. Clean
merges dispatch nothing.

### In backports

After the cherry-pick, the **line's own** `formatting.yml` runs on the backport branch, with the line's tip, the picked
commit and its parent as the three commits. When the backport rewrote the line's platform values (lockfile reset,
platform markers), the cherry-picked side exists only in the git writer and cannot be proven. The pull request then
opens with a **Not regenerated** note listing the files to fix by hand.

## Troubleshooting

| Outcome | Cause | What to do |
|---|---|---|
| "set up Formatting first" | No `formatting.yml` on the default branch. | Run **Set up features** with Formatting and merge it. |
| "already formatted" | The run's patch was empty. | Nothing. |
| "its patch was refused (…)" | The patch touched an excluded path, added, deleted or renamed a file. | Check `exclude` and the formatter's config. |
| "`<branch>` moved, run it again" | The default branch got a new head during the run. | Run the action again. |
| Evicted after a `whitespace` resolution | The proof failed: the conflict changed more than formatting, the pull request changed the formatter's inputs, or a conflicted file is excluded or has no formatter. | Rebase by hand. |
