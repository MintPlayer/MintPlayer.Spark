# SeoTester for AI agents (MCP)

> **Draft.** Planned in [spark_mcp_PRD.md](../spark_mcp_PRD.md) §4.7; not deployed yet.

Let your coding agent read SeoTester's findings for your sites and fix them in your code, then start a new
scan to check the fix.

**Server address:** `https://seotester.mintplayer.com/mcp`

How to add it to Claude Code, Codex or Gemini and sign in: [Connect an AI agent](../guide-mcp-agents.md).
You sign in with the same account you use on the website (Microsoft, GitHub, Google or a passkey). The
agent sees the sites of your organizations only, with the same roles you have on the website.

## Tools

| Tool | What it gives the agent |
|---|---|
| `seotester_list_sites` | Your organizations' sites with verified state, latest score and grade. |
| `seotester_run_summary` | Category scores and every check of a scan: rule, severity, status, affected pages, new/fixed since the previous scan. |
| `seotester_list_findings` | The findings per page, filtered by severity, category, rule or URL prefix, with the affected element where known. |
| `seotester_explain_check` | Why a rule matters, how to fix it, and ready-made snippets for nginx, Apache, IIS and HTML. Works without signing in. |
| `seotester_get_page` | Title, description, H1, Open Graph tags and findings of one page. |
| `seotester_start_scan` | Starts a new scan (needs changes allowed for this agent, and the same organization role as the Scan button). |
| `seotester_scan_status` | Progress of a running scan. |

## Example session

Open your agent **in your website's repository**, then ask:

> Using SeoTester, list the critical and high findings of the latest scan of example.com. Fix the ones
> you can fix in this repository, explain the ones that need server configuration, then start a new scan.

The agent typically:

1. calls `seotester_list_sites` and `seotester_run_summary` to find the scan;
2. calls `seotester_list_findings(minSeverity: High)`;
3. calls `seotester_explain_check` per rule for the fix and snippets;
4. searches your repository for the page templates behind each URL and edits them;
5. for server rules (security headers, redirects, TLS), proposes the nginx/IIS/Apache snippet instead;
6. calls `seotester_start_scan` and `seotester_scan_status`, and reports what was fixed.

## Good to know

- **Per-page findings are kept for 7 days.** After that only the totals of a scan remain; the agent will
  say so and can start a new scan.
- **Website text is untrusted.** Titles, descriptions and snippets come from the crawled site and are
  passed to the agent marked as data. If your site was tampered with, its text can't steer the agent into
  doing something else, but always read what the agent wants to change.
- The agent doesn't know which file renders which URL; it searches your repository. Run it from there.
