# CodeCoverage for AI agents (MCP)

> **Draft.** Built (plan M6, [spark_mcp_PRD.md](../spark_mcp_PRD.md) §4.8); not deployed yet. This banner goes once
> every example below has run against coverage.mintplayer.com.

Let your coding agent ask CodeCoverage where coverage is missing, then write the tests in your checkout.

**Server address:** `https://coverage.mintplayer.com/mcp`

How to add it to Claude Code, Codex, Gemini or VS Code: [Connect an AI agent](../guide-mcp-agents.md).

## Signing in

- **Public repositories work without signing in.**
- **For your private repositories, let the agent sign you in.** It opens CodeCoverage's sign-in page in your browser;
  you sign in with GitHub, as on the website, and choose what the agent may read. It then sees exactly the repositories
  you see on the website, and nothing it may not: a repository you can't see answers like one that doesn't exist.
- **Or use a personal access token**, for an agent that can't open a browser: Account → Connected applications →
  create a token (`spat_…`, shown once, 90 days by default), and configure it as the header
  `Authorization: Bearer spat_…`.
- Upload tokens (`covt_…`) and GitHub Actions tokens don't work here: `/mcp` refuses them (401). They are for uploads
  only.
- Account → Connected applications lists every agent and token; revoking one stops it at its next call.

Everything is read-only: no agent can change anything in CodeCoverage.

## Tools

Name a repository as `forge/owner/name`, the website's own URL segments: `github/MintPlayer/MintPlayer.Spark`.
`codecoverage_list_repositories` shows the exact value.

| Tool | Arguments | What it gives the agent |
|---|---|---|
| `codecoverage_list_repositories` | `search?`, `skip`, `take` (1–50) | The repositories you can see, with line and branch coverage on the default branch. |
| `codecoverage_summary` | `repository`, `branch?` | Totals on the branch (the default branch reads the commit the badge shows), the trend over the last 30 commits with coverage, the largest drops among them, and the branches that have coverage. |
| `codecoverage_hotspots` | `repository`, `branch?`, `pathPrefix?`, `skip`, `take` (1–50) | The latest commit's files ranked by **uncovered lines, most first**, with the covered percentage and the number of uncovered line ranges as columns. |
| `codecoverage_file` | `repository`, `path`, `commit?` | One file's uncovered line ranges and the lines whose branches were only partly taken. Line numbers only: the agent reads the source in your checkout. |
| `codecoverage_patch` | `repository`, `pullRequest` | Patch coverage of a pull request (its newest commit with coverage) and, per file, the added lines tests didn't run. |
| `codecoverage_drops` | `repository`, `branch?`, `since?`, `take` (1–10) | Commits that lowered coverage, each with the files whose uncovered lines grew most. |

The server also has Spark's generic read tools over repositories and commits: `codecoverage_discover`,
`codecoverage_describe`, `codecoverage_query` and `codecoverage_get`.

## Prompt

`codecoverage_raise_coverage` (`repository`, `branch?`) asks the agent to raise coverage step by step: summary, hotspots,
the chosen file's uncovered ranges, tests next to the existing ones, then a report. In Claude Code it is the command
`/mcp__<server name>__codecoverage_raise_coverage`.

## Example sessions

In the repository, on the branch you want to improve:

> Using CodeCoverage, find the five files in github/MintPlayer/MintPlayer.Spark with the most uncovered lines. For
> each, show the uncovered ranges, then write tests for the first one.

> My pull request #123 has low patch coverage. Which added lines are uncovered? Add tests for them.

The agent calls `codecoverage_hotspots`, then `codecoverage_file` per file, reads those line ranges in your local
checkout, and writes tests next to the existing ones.

## Good to know

- **The ranking is plain uncovered lines.** An "ease" score (prefer files that already have tests and few gaps) was
  measured against the last 60 pull requests that added tests to this repository; it did worse than ranking by
  uncovered lines ([spike S8](../spark_mcp_spikes.md#s8--does-the-hotspot-ranking-predict-cheap-gains)). The agent sees
  the covered percentage and the ranges and can weigh them itself.
- Coverage reflects the **last uploaded commit** of the branch. Commit and push first, wait for the upload, then ask
  again to see the effect of new tests.
- A repository you can't see answers exactly like one that doesn't exist: "not found" when you are signed in, a request
  to sign in when you aren't.
- File paths, branch names and commit messages come from whoever pushed to the repository. The server marks every
  answer as data, so the agent doesn't follow instructions written into them.
- Operators: configuration, signing keys and proxy notes are in [hosting.md](hosting.md#ai-agents-mcp).
