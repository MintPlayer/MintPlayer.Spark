# Nx remote cache

The repo uses a self-hosted Nx remote cache server to share build/test outputs across CI runs (and across machines for any maintainer who exports the same env vars locally). Master pushes populate the cache; PRs read from it.

## Configuration

All three workflows in `.github/workflows/` set:

```yaml
env:
  NX_SELF_HOSTED_REMOTE_CACHE_SERVER: ${{ secrets.NX_CACHE_SERVER }}
  NX_SELF_HOSTED_REMOTE_CACHE_ACCESS_TOKEN: ${{ (github.event_name == 'push' && github.ref_name == github.event.repository.default_branch) && secrets.NX_CACHE_RW_TOKEN || secrets.NX_CACHE_RO_TOKEN }}
```

`NX_CACHE_SERVER`, `NX_CACHE_RW_TOKEN`, and `NX_CACHE_RO_TOKEN` are configured as **organization-level secrets** with visibility to public repos in the MintPlayer org — no per-repo setup required.

The token gating is the important bit:

| Trigger | Token |
| --- | --- |
| Push to `master` | `NX_CACHE_RW_TOKEN` (read + write) |
| Push to a branch in this repo | `NX_CACHE_RO_TOKEN` (read only) |
| PR from a branch in this repo | `NX_CACHE_RO_TOKEN` (read only) |
| PR from a fork | *no token* — secrets are never passed to fork-triggered workflows by GitHub, so the cache is unreachable and Nx falls back to the runner's local cache |

So only post-merge runs on the default branch can write into the cache.

## Security: cache poisoning consideration

If a contributor with **write access to the repo** can push a branch and run CI on it with the RW token, they can plant a malicious build artifact in the cache. A later release build that hits the same cache key would replay that poisoned artifact instead of rebuilding — bypassing code review.

The expression above blocks that vector for fork PRs (forks never see secrets) and for branch pushes from non-default branches (RO token only). The remaining trust boundary is **direct push access to `master`**.

### Current threat model for this repo

- The MintPlayer organization currently has a single member (the maintainer).
- External contributions must arrive as fork PRs — and GitHub does not pass secrets to fork-triggered workflows, so a fork PR cannot acquire either Nx token (it can't even read the cache, let alone write to it).
- Therefore the cache-poisoning vector is not exploitable today.

### What to revisit if the org grows

If additional members are granted write access to this repo, reconsider one or more of:

1. **Keep RW restricted to master pushes only** (already in place — don't loosen).
2. **Require all master pushes to go through PR review** (branch protection).
3. **Disable remote-cache reads for release-publishing steps** — e.g. add `--skip-nx-cache` to the `dotnet pack` / `npm publish` build steps in `dotnet-build-master.yml` so the bits being shipped are always rebuilt from source on the runner.
4. **Use a separate cache namespace for release builds** so day-to-day CI cache hits can't influence what gets published.

Option 3 is the cheapest mitigation if you ever need it — it costs ~1 build of cache rebuild on every release but guarantees the published artifact wasn't sourced from a cached entry written by a less-trusted run.

## Local use

To benefit from the remote cache locally (read-only is fine):

```bash
export NX_SELF_HOSTED_REMOTE_CACHE_SERVER=<cache-server-url>
export NX_SELF_HOSTED_REMOTE_CACHE_ACCESS_TOKEN=<read-only-token>
npx nx run-many -t build
```

Without these set, Nx falls back to the local-only cache under `.nx/cache/`.
