# MintPlayer dependency updates: the GitHub Action

`MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action` is the runner half of
[CodeCoverage's dependency updates](../../../docs/code-coverage/dependency-updates.md): one rolling pull request per
repository, with every restore, relock and migration running in the repository's own GitHub Actions.

## What a repository needs

Only the workflows in [`templates/`](templates), and the onboarding pull request adds them for you
(*Enable dependency updates* on `https://coverage.mintplayer.com/dependency-updates/onboard`):

| Workflow | Dispatched by the bot | Does |
|---|---|---|
| `dependency-lookup.yml` | on the default branch | Runs Renovate in lookup-only mode and uploads `lookup.json`. |
| `dependency-apply.yml` | on the bot's plan ref | Edits the manifests, pins and relocks with scripts off, and uploads the patch the bot validates and pushes. |
| `angular-migrate.yml`, `nx-migrate.yml` | for an approved group | Run `ng update` / `nx migrate` migrations (only added for `angular.json` / `nx.json`). |

No secret is needed, except the lookup's optional `host-rules` for private feeds. Keep the workflows read-only
(`contents: read`, no `id-token`, `persist-credentials: false`): the bot refuses to dispatch one that may write.

## Versions

Workflows pin the action to a commit with its release line as a comment:

```yaml
uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action@<sha> # dependency-update-v1
```

The rolling pull request moves that pin to each new release on the `dependency-update-v1` line, without the cooldown
other updates wait for. `dist/` is the committed ncc bundle; build it from the main checkout only (see the repository's
`CLAUDE.md`).
