# The RavenDB test server and the Nx cache

`RavenDB.TestDriver` runs a real RavenDB server for tests. That server is **623 MB**, and getting it
into the wrong place breaks CI in a way that looks like something else entirely. This is how it is
wired here and why.

If you are adding a test project that references `RavenDB.TestDriver`, you need the
[Adding a test project](#adding-a-test-project) section. Everything else is the reasoning.

---

## The short version

- The server is **never** copied into `bin/`. Two MSBuild targets in `Directory.Build.targets` stop
  it.
- `RavenServerLocator` copies it once into a temp directory and hands the path to
  `ServerDirectory`.
- Do not "fix" a missing server by re-enabling the copy into `bin/`. See
  [Why not just exclude it](#why-not-just-exclude-it-from-the-cached-outputs).

## What goes wrong if it lands in bin/

`RavenDB.Embedded` copies its `RavenDBServer` tree into `$(OutDir)` on every build, and Nx's
inferred `build` target declares the output as:

```json
"outputs": ["{projectRoot}/bin/Debug", "{projectRoot}/obj/Debug"]
```

So the 623 MB becomes a cache artifact. Pushing that to the self-hosted cache aborts — nginx logs
`499`, client closed request — and Nx reports:

```
Misconfigured remote cache endpoint: Unexpected response status: 499 <unknown status code>
```

**Nx escalates that into a task failure.** The `build` task is marked failed, and every task
depending on it is skipped:

```
NX   Running target test for 12 projects and 34 tasks they depend on failed

Tasks not run because their dependencies failed or --nx-bail=true:
- CodeCoverage.Tests:test
- MintPlayer.Spark.Tests:test
...
Failed tasks:
- MintPlayer.Spark.Testing:build
- CodeCoverage.Tests:build
```

The trap is that those builds **compiled cleanly** — `0 Error(s)` in the MSBuild output. A cache
problem is presented as a build failure, with a green compiler log and no test results at all. On a
`master` push the same upload runs with the RW token, so it also blocks publishing.

Measured before the fix: **272 cache entries containing the server, 64.3 GB — 93% of a 69 GB
volume.** Retention is 14 days, so it rebuilds itself over any fortnight.

> **Diagnosing this from the symptom:** if `nx run-many --target=test` fails on a `build` task whose
> compiler output is clean, check for `Misconfigured remote cache endpoint` before you look at the
> code. And capture the exit code properly — `cmd > log; echo $?` reports `echo`'s status, not the
> command's, which is how a failed sweep once got reported as passing.

## How it works now

### 1. Two copy routes, both suppressed

The server arrives by **two independent mechanisms**, and closing only one looks like a no-op —
this cost a debugging round, so it is worth stating plainly:

| Route | Mechanism |
|---|---|
| NuGet `contentFiles` | The nuspec marks the tree `buildAction="None" copyToOutput="true"`, so the files become `None` items and the ordinary copy-to-output pass moves them. |
| The package's own target | `RavenDB.Embedded.targets` defines `CopyRavenDBServer`, hooked into `PrepareForRunDependsOn`. |

`Directory.Build.targets` empties the item list for both. It removes items rather than overriding
`CopyRavenDBServer`, because an override would depend on import order between
`Directory.Build.targets` and NuGet's generated `.nuget.g.targets`, whereas an empty item list is
order-independent and degrades to a no-op if the package ever stops defining them.

### 2. Provisioned at run time

`libs/testing/MintPlayer.Spark.Testing/RavenServerLocator.cs` copies the server from the restored
NuGet package into:

```
%TEMP%/MintPlayer.Spark/RavenDBServer/<package-version>/
```

and exposes it as `RavenServerLocator.ServerDirectory`. It is copied once per machine per version.

Two separate concurrency problems are handled, because `nx run-many` runs test projects as parallel
processes and three of them (`MintPlayer.Spark.Tests`, `MintPlayer.Spark.Client.Tests`,
`MintPlayer.Spark.E2E.Tests`) resolve the same version through `MintPlayer.Spark.Testing`:

- **Nobody may observe a partial tree.** The copy goes into `<target>.staging-<pid>`, the
  `.spark-provisioned` marker is written *inside* staging, and the whole directory is published
  with a single `Directory.Move`. The marker only becomes visible through the rename, so a reader
  sees either no directory or a complete one.
- **Only one process should do the copying.** An exclusive lock file serialises provisioning; the
  others wait for the marker instead of each copying 623 MB. Without it a cold machine does ~1.9 GB
  of simultaneous I/O, which was measured starving the E2E suite into timing-related failures. The
  lock is a file rather than a named mutex because named mutexes are Windows-only in .NET and CI
  runs on Linux, and it uses `FileOptions.DeleteOnClose` so a killed holder cannot wedge later
  runs. A waiter that times out after 10 minutes copies anyway, so a stuck holder degrades to the
  old behaviour rather than hanging.

Staging directories abandoned by a process that died mid-copy are cleaned up on the next
provisioning run.

If provisioning fails, `ServerDirectory` is `null`. **That is not a working fallback** — RavenDB
then looks in `AppContext.BaseDirectory`, which no longer contains a server, and fails to start.
`null` is returned rather than thrown so the error surfaces from RavenDB's own start-up naming the
missing directory, instead of as a `TypeInitializationException` from a static constructor. If the
suite fails with the server not found, provisioning is what to investigate.

**Version-scoped** because projects here pin different `RavenDB.TestDriver` versions —
`MintPlayer.Spark.Testing` on 7.2.5, `CodeCoverage.Tests` on 7.2.1 — and two server builds must not
share a directory. The version is read from the test assembly's `deps.json` at run time, not baked
in by MSBuild: an absolute path compiled into an assembly would travel inside the Nx cache and be
wrong on the next machine that replayed it.

## Adding a test project

If your project references `RavenDB.TestDriver`, set `ServerDirectory` when you configure the
server:

```csharp
ConfigureServer(new TestServerOptions
{
    ServerDirectory = RavenServerLocator.ServerDirectory,
    // ... licensing etc.
});
```

The MSBuild suppression is repo-wide and needs no per-project setup.

To reach the locator, prefer a `ProjectReference` to `MintPlayer.Spark.Testing`. If your project
pins a *different* `RavenDB.TestDriver` version, link the source file instead — a `ProjectReference`
would silently bump the server version underneath your tests:

```xml
<Compile Include="..\..\..\libs\testing\MintPlayer.Spark.Testing\RavenServerLocator.cs"
         Link="RavenServerLocator.cs" />
```

`apps/CodeCoverage/CodeCoverage.Tests` does exactly this, for exactly that reason.

## Why not just exclude it from the cached outputs?

The obvious fix is a negated output glob:

```json
"outputs": ["{projectRoot}/bin/Debug", "!{projectRoot}/bin/Debug/**/RavenDBServer/**"]
```

**It does not work, and would not be enough if it did.**

- [nrwl/nx#35150](https://github.com/nrwl/nx/issues/35150) reports that negated `outputs` patterns
  do not prevent caching of the excluded directory. Open since 2026-04-02; the fix,
  [#35152](https://github.com/nrwl/nx/pull/35152), is unmerged. Their reproduction is this problem
  down to the size — a 600 MB `.next/cache` that refuses to stay out.
- Even with it working, *"excluded from the cache"* and *"not needed after a replay"* are different
  properties. The `test` task runs `dotnet test` in no-build mode, so on a `build` cache **hit**
  MSBuild never runs. The restored `bin/` would come back without the server and the tests would
  fail on a fresh runner — a cache-hit-only failure, which is worse than the original bug because
  it is intermittent.

That second point rules out any build-time relocation too, not just exclusion. Whatever produces the
server has to run when the build does not, which is why provisioning happens at run time.

## Why not point ServerDirectory at the NuGet package?

Tempting — the binaries are already there after `dotnet restore`, so no copy is needed at all.

**Measured 2026-09-10: the embedded server creates a `Temp` directory next to its own binaries at
startup.** That would write into the machine-wide package store shared by every solution on the
machine. `~/.nuget/packages` is content-addressed and treated as immutable;
[nrwl/nx#36902](https://github.com/nrwl/nx/issues/36902) documents the same hazard from the other
end, where a cached `obj/` from another user poisons a build.

So the server directory must be **writable and private**, which means a copy.

## Related

- [Nx remote cache](guide-nx-remote-cache.md) — cache server configuration and token scoping.
- `docs/prd/PRD-SlingBot-Archival-Followups.md` (D9) — the full measurement record.
- Issue #402 — the defect this guide documents.
