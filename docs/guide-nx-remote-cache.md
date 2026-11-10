# Nx remote cache

The repo uses a self-hosted Nx remote cache server to share build/test outputs across CI runs (and across machines for anyone who exports the server URL locally). Master pushes populate the cache; **everyone can read it**, fork pull requests included.

## Server: public.nx-cache.mintplayer.com (since 2026-11-10)

The server runs on the production VPS in `/var/www/public-nx-cache` (Traefik routes on the request, the tokens are in
that directory's `.env`). It replaced `nx-cache.mintplayer.com`, which needed a read-only token to read; that host
name was removed so nobody mistakes the new one for a token-protected cache.

| Request | Result |
| --- | --- |
| `GET`/`HEAD`, any or no token | served (public read) |
| `PUT` with `NX_CACHE_RW_TOKEN` | stored |
| `PUT` without it | answered `200` by a sink and **discarded**: Nx 23 treats a refused write (403) as a fatal "Misconfigured remote cache endpoint" |
| anything else | `403` |

Reads are public because everything cached is built from public source: the MintPlayer organization has no private
repositories. **If a private repository ever uses this cache, its outputs become downloadable by hash.** Nx also caches
each task's terminal output, so anything a task prints is public too.

## Configuration

The workflows in `.github/workflows/` set:

```yaml
env:
  NX_SELF_HOSTED_REMOTE_CACHE_SERVER: ${{ secrets.NX_CACHE_SERVER }}
  NX_SELF_HOSTED_REMOTE_CACHE_ACCESS_TOKEN: ${{ (github.event_name == 'push' && github.ref_name == github.event.repository.default_branch) && secrets.NX_CACHE_RW_TOKEN || '' }}
```

`NX_CACHE_SERVER` and `NX_CACHE_RW_TOKEN` are **organization-level secrets** visible to every repository in the
MintPlayer org. `NX_CACHE_RO_TOKEN` is retired: the server ignores it.

| Trigger | Token | Effect |
| --- | --- | --- |
| Push to `master` | `NX_CACHE_RW_TOKEN` | read + write |
| Any other push or a PR from a branch in this repo | none | read; writes discarded |
| PR from a fork | none (GitHub passes no secrets) | read; writes discarded. Before 2026-11-10 forks could not read at all, because they could not get the read token |

## Security: cache poisoning consideration

Whoever can write can plant an artifact under the cache key a later build computes, and that build replays it instead
of rebuilding: Nx's [CREEP, CVE-2025-36852](https://nx.dev/blog/cve-2025-36852-critical-cache-poisoning-vulnerability-creep)
("first to cache wins").

**The expression above is not the boundary.** A `pull_request` run uses the workflow file of the pull request itself,
so a pull request from a branch in this repository can edit the expression, and `secrets: inherit` hands it every
secret, `NX_CACHE_RW_TOKEN` included. What GitHub itself enforces is only that **fork** pull requests get no secrets.
The real trust boundary is therefore **push access to a branch of this repository**: the maintainer, and the Apps that
push branches here (the dependency updater's pull requests install new package versions, whose install scripts run in
that job). To move the boundary to GitHub, put `NX_CACHE_RW_TOKEN` in an environment that allows only `master` and give
the writing jobs `environment:`; a pull request run is then refused the environment.

### Current threat model for this repo

- The MintPlayer organization currently has a single member (the maintainer).
- External contributions arrive as fork PRs, which never see the write token.
- The remaining exposure is a branch pushed by an App, such as a dependency update carrying a compromised package.

### What to revisit if the org grows

If additional members are granted write access to this repo, reconsider one or more of:

1. **Keep RW restricted to master pushes only** (already in place — don't loosen).
2. **Require all master pushes to go through PR review** (branch protection).
3. **Disable remote-cache reads for release-publishing steps** — e.g. add `--skip-nx-cache` to the `dotnet pack` / `npm publish` build steps in `dotnet-build-master.yml` so the bits being shipped are always rebuilt from source on the runner.
4. **Use a separate cache namespace for release builds** so day-to-day CI cache hits can't influence what gets published.

Option 3 is the cheapest mitigation if you ever need it — it costs ~1 build of cache rebuild on every release but guarantees the published artifact wasn't sourced from a cached entry written by a less-trusted run.

## Local use

To benefit from the remote cache locally, only the server is needed (writes are discarded without the write token):

```bash
export NX_SELF_HOSTED_REMOTE_CACHE_SERVER=https://public.nx-cache.mintplayer.com
npx nx run-many -t build
```

Without these set, Nx falls back to the local-only cache under `.nx/cache/`.

## Keeping large build products out of the cache

Nx has **no size-based filter** on what is uploaded. `maxCacheSize` / `NX_MAX_CACHE_SIZE` caps the
*local* cache directory and evicts least-recently-used entries; it never inspects a remote upload.
The only filter is the `outputs` globs, which select by path — and negated patterns are currently
[reported not to work](https://github.com/nrwl/nx/issues/35150).

This matters because a failed cache upload is not reported as a cache problem: **Nx escalates it
into a task failure**, so a `build` task fails with a clean compiler log and every dependent test
task is skipped.

The one instance of this in the repo — RavenDB's 623 MB embedded test server, which once occupied
93% of the cache volume — is documented in
[the RavenDB test server guide](guide-ravendb-test-server.md). Read it before adding a test project
that references `RavenDB.TestDriver`, or before declaring outputs for any target that emits
hundreds of megabytes.
