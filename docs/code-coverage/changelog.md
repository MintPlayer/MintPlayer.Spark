# Changelog

CodeCoverage writes each package's release notes from the pull requests merged since its last release. It builds on
[package versioning](versioning.md): the notes live in the version tags and in the GitHub Releases, and **no changelog
file is committed**. Design and decision log: [`../coverage_changelog_PRD.md`](../coverage_changelog_PRD.md). In-app
guide: `/versioning/setup#changelog`.

## Switch it on

The **Changelog** policy is on the account's and the repository's **Versioning** tab (Inherit / Enabled / Disabled; the
account default is off). Switching it on also switches on pull-request title correction, which edits other people's
pull-request titles; a repository can still turn title correction off on its own.

Onboarding, with versioning picked and the changelog on, also:

- adds a "Type of change" section to the pull-request template (`.github/pull_request_template.md`, created when the
  repository has none). Switch **PR template** off on the Versioning tab to keep the bot out of the template. A template
  that has the section already is left alone, and the bot never edits it between onboardings.
- lists the packages that don't name their repository (`<RepositoryUrl>` or `PublishRepositoryUrl` in the csproj, often
  once in `Directory.Build.props`; `repository` in `package.json`). Renovate and Dependabot find the release notes through
  it.

## What a release's notes contain

Each package Release (`<PackageId> <version>`) has these sections, each only when it has entries:

| Section | From |
|---|---|
| Breaking changes | a `!` in the type (`feat!:`), and public API removed since the last version |
| Features | `feat` |
| Fixes | `fix` |
| Performance | `perf` |
| Reverts | `revert` |
| Changes | the types listed in `versioning.changelog.include` |
| Public API | public API added since the last version (NuGet and npm listings) |

`build`, `chore`, `ci`, `docs`, `refactor`, `style` and `test` are left out unless `versioning.changelog.include` lists
them:

```yaml
versioning:
  changelog:
    include: [docs, refactor]
```

A package released only because one of its dependencies moved gets no Release of its own. Every `/release` run (or push
release, without release lines) also creates **one run Release**, marked latest, that lists every package it released
with links to their Releases; its tag is `release/<line>/<yyyy-MM-dd>` (or `release/<yyyy-MM-dd>` without lines) and
the versioning engine ignores it.

The notes are frozen in the version tag's annotation when the tag is written. NuGet packages also carry them as
`PackageReleaseNotes` (Visual Studio's NuGet window shows them), truncated to nuget.org's 35,000 characters with a link
to the Release.

## Where an entry's type and text come from

The type comes from an **explicit signal** only, in this order:

1. the button a maintainer clicked on the title check (Feature, Fix, Not user-facing);
2. the ticked box in the pull request's "Type of change" section;
3. the type in the title (`fix: …`);
4. a `bug` label on an issue the pull request closes (`Closes #12`) → `fix`.

The branch name is never read, and there is no `feat` default. Without a signal the title stays untyped and the title
check asks with the three buttons. Perf, revert and breaking still need a box or a type in the title. Only people with
write access can click the buttons.

The text is the pull request's corrected title, unless its body has a `## Changelog` section: each bullet there is one
entry and may carry its own type (`- fix: …`). An empty section, or `none`, means no entry. The `changelog: skip` label
leaves the pull request out. The body is read when the release runs, so it can be corrected until then.

## On CodeCoverage

The repository page has two changelog queries:

- **The released notes**: every package Release's notes, newest first, copied when GitHub reports the Release published
  or edited.
- **The upcoming notes**: per release line (and the default branch), what the next release would list, recomputed on
  every push to it. A line is recognised once the default branch has been composed once, since that is where the bot
  reads the line pattern from.

The upcoming notes are composed from the commits alone: the bot's git job may only read the repository contents, not
pull requests. A `## Changelog` section or a skip label therefore shows only in the released notes.

## Troubleshooting

| Symptom | Cause |
|---|---|
| A pull request has no entry | It is a left-out type (`docs`, `chore`, …), has `changelog: skip`, or has an empty `## Changelog` section. |
| The title check keeps asking | No explicit signal: tick a box, type the title, or click a button. |
| A Release has no notes on CodeCoverage | The changelog was off when it was published; an edit of the Release copies it. |
| The upcoming view is empty for a line | Nothing has been pushed to the line since the changelog was switched on, or nothing would release. |
| The template got no section | The PR template policy is off, or the template has a "Type of change" heading already. |
