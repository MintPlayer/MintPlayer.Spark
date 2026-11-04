# NuGet trusted publishing

The master workflow (`.github/workflows/dotnet-build-master.yml`, job `publish`) pushes the NuGet
packages to nuget.org **without a stored API key**. GitHub Actions issues the job an OIDC token, and
nuget.org exchanges it for a short-lived API key (valid about an hour) after checking the token
against a trusted-publishing policy.

## Why

The long-lived key in the `PUBLISH_TO_NUGET_ORG` secret expired (2026-11-04). The tests, release and
tags succeeded, but the master run failed at `PushNuget`. With trusted publishing there is no key to
expire, rotate or leak.

## The nuget.org policy

Created at <https://www.nuget.org/account/trustedpublishing>, signed in as **Pieterjan** (a member of the
`MintPlayer` organisation). Open **Create** and fill in:

| Field | Value | Notes |
|---|---|---|
| Policy Name | `MintPlayer.Spark publish-release` | Any name. |
| Package Owner | `MintPlayer` | The organisation that owns every package below (checked through the nuget.org search API's `owners`). |
| CI/CD Provider | GitHub Actions | |
| Repository Owner | `MintPlayer` | nuget.org stores the permanent id as well (#48772716). |
| Repository | `MintPlayer.Spark` | Permanent id #1006469943. A renamed or recreated repo does not match. |
| Workflow File | `dotnet-build-master.yml` | File name only, no `.github/workflows/` prefix. It is the top-level workflow that runs the `publish` job. |
| Environment | *(empty)* | The `publish` job does not use a GitHub environment. If one is added, it must be entered here too. |
| Scopes | Push → "Push new packages and package versions" | New packages need this, because the versioning engine creates them (`docs/coverage_package_versioning_PRD.md`). |
| Glob patterns | `MintPlayer.Spark*`, `MintPlayer.Dotnet.SocketExtensions`, `MintPlayer.Versioning.MSBuild` | One per line: every package id this repo tags (`git tag -l '*/v*'`), minus the npm and schema tags. |

**A package outside these patterns is refused.** When the repo starts publishing a package with a
new id prefix, add its pattern to the policy (**Manage** → **Edit**) before the first master run that
ships it.

## The workflow

The `publish` job already had `id-token: write`, which the OIDC token needs. Before `PushNuget`:

```yaml
- name: NuGet login (trusted publishing)
  id: nuget-login
  uses: NuGet/login@v1
  with:
    user: Pieterjan   # the nuget.org profile that owns the policy, not an e-mail address

- name: PushNuget
  run: dotnet nuget push 'nupkgs/*.nupkg' --source https://api.nuget.org/v3/index.json --api-key ${{ steps.nuget-login.outputs.NUGET_API_KEY }} --skip-duplicate
```

`user` is the account that created the policy. If the policy is recreated under another account, change
`user` with it.

## Housekeeping

- After the first successful trusted publish, delete the repository secret `PUBLISH_TO_NUGET_ORG`
  (Settings → Secrets and variables → Actions). Nothing else reads it. The `PUBLISH_TO_NUGET_ORG` in
  `apps/CodeCoverage/dependency-action/test/fixtures/` is a test fixture, not a reference.
- The push to GitHub Packages (`PushGithub`) and the npm publish are unchanged.

## Troubleshooting

- **401 / "no matching trusted publishing policy"**: compare the run's repository, workflow file and
  environment with the policy. A typo in the workflow file name or an added environment are the usual
  causes.
- **403 for one package**: its id matches none of the glob patterns, or the package is not owned by
  `MintPlayer`.
- **`NuGet/login` fails to get a token**: the job lost `id-token: write`.
