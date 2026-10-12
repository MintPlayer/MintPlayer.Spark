# CodeCoverage for AI agents (MCP)

> **Draft.** Planned in [spark_mcp_PRD.md](../spark_mcp_PRD.md) §4.8; not deployed yet.

Let your coding agent ask CodeCoverage where coverage is missing and which tests would raise it most, then
write those tests.

**Server address:** `https://coverage.mintplayer.com/mcp`

How to add it to Claude Code, Codex or Gemini and sign in: [Connect an AI agent](../guide-mcp-agents.md).
Public repositories work without signing in. For your private repositories the agent opens your browser
and you sign in with GitHub, as on the website; it then sees the repositories you can see there. Upload
tokens (`covt_…`) and GitHub Actions tokens don't work on this endpoint; they are for
uploads only.

## Tools

| Tool | What it gives the agent |
|---|---|
| `codecoverage_list_repositories` | Your repositories with line and branch coverage. |
| `codecoverage_summary` | Totals for a branch, the trend over the last 30 commits, the largest drops. |
| `codecoverage_hotspots` | Files ranked by expected coverage gain per unit of effort, with the gain if fully covered. |
| `codecoverage_file` | Uncovered line ranges and partly covered branches of one file (line numbers only). |
| `codecoverage_patch` | Patch coverage of a pull request and its uncovered changed lines. |
| `codecoverage_drops` | Commits that lowered coverage and the files responsible. |

All tools are read-only.

## Example sessions

In the repository, on the branch you want to improve:

> Using CodeCoverage, find the five files in this repository where tests would raise line coverage most
> cheaply. For each, show the uncovered lines, then write tests for the first one.

> My pull request #123 has low patch coverage. Which changed lines are uncovered? Add tests for them.

The agent calls `codecoverage_hotspots`, then `codecoverage_file` per file, reads those line ranges in your
local checkout, and writes tests next to the existing ones.

## Good to know

- **"Cheaply" is a heuristic:** files that already have some tests and whose uncovered lines sit in a few
  blocks rank higher. The tool description states the formula.
- Coverage reflects the **last uploaded commit** of the branch. Commit and push first, wait for the upload,
  then ask again to see the effect of new tests.
- A repository you can't see answers "not found", exactly like one that doesn't exist.
