# SeoTester for AI agents (MCP)

> **Draft.** Built in plan M5 of [spark_mcp_plan.md](../spark_mcp_plan.md) ([spark_mcp_PRD.md](../spark_mcp_PRD.md) §4.7);
> not deployed yet (plan M9). Remove this banner once every step below has run against seotester.mintplayer.com.

Let your coding agent read SeoTester's findings for your sites and fix them in your code, then start a new scan to check
the fix.

**Server address:** `https://seotester.mintplayer.com/mcp`

How to add it to Claude Code, Codex, Gemini or VS Code and sign in: [Connect an AI agent](../guide-mcp-agents.md). Two ways:

- **Sign in from the agent** (recommended). The agent opens SeoTester's sign-in in your browser; you sign in with the same
  account you use on the website (Microsoft, GitHub or Google) and choose what the agent may see and
  whether it may start scans.
- **A personal access token**, for agents that cannot open a browser: **Account → Connected applications → Create token**.
  It starts with `spat_`, is shown once, and expires (90 days by default, at most a year). Give it to the agent as a
  bearer header.

The agent sees the sites of your organizations only, with the same roles you have on the website. **Account → Connected
applications** lists every agent you connected: change what it may see or do, or disconnect it; the next call follows.

## Tools

| Tool | What it gives the agent |
|---|---|
| `seotester_list_sites` | Your organizations' sites: address, whether ownership is verified, latest score and grade, last scan. |
| `seotester_run_summary` | One scan (the latest finished one unless the agent names another): category scores and every rule with its severity, status, affected pages, up to five sample URLs and New/Fixed/Unchanged since the previous scan. |
| `seotester_list_findings` | The failing and warning rules page by page, grouped by rule, with what is wrong and the affected elements as CSS selectors. Filters: minimum severity, category, rule, URL prefix. It reads up to 200 pages a call and says where to continue. |
| `seotester_get_page` | One page: status code, load time, title, description, H1, Open Graph tags and its findings. |
| `seotester_explain_check` | Why a rule matters, how to fix it, and ready-made snippets for nginx, Apache, IIS and HTML. Works without signing in. |
| `seotester_start_scan` | Starts a new scan, or joins the one already running for the site. Only for an agent you allowed to make changes, and only where the Scan button works for you: the Member role in the site's organization, your scan quota, and for an unverified site the 6-hour pause between scans. |
| `seotester_scan_status` | Status, phase and progress of a scan, and its score once finished. |

The agent can name a site by the id `seotester_list_sites` gives (`Sites/…`) or by its address (`example.com`).

## Prompt

`fix_seo_findings` runs the whole session below. In Claude Code: `/mcp__seotester__fix_seo_findings example.com High`
(the site and the lowest severity to fix are optional).

## Example session

Open your agent **in your website's repository**, then ask:

> Using SeoTester, list the critical and high findings of the latest scan of example.com. Fix the ones
> you can fix in this repository, explain the ones that need server configuration, then start a new scan.

The agent typically:

1. calls `seotester_list_sites` and `seotester_run_summary` to find the scan;
2. calls `seotester_list_findings(minSeverity: High)`, following `nextSkip` for large sites;
3. calls `seotester_explain_check` per rule for the fix and snippets;
4. searches your repository for the templates behind each URL and element and edits them;
5. for server rules (security headers, redirects, TLS), proposes the nginx/IIS/Apache snippet instead;
6. once your fix is live, calls `seotester_start_scan` and `seotester_scan_status`, and reports what changed.

## Good to know

- **Per-page findings are kept for 7 days.** After that only the totals of a scan remain (affected pages, sample URLs);
  the agent says so and offers a new scan. An expired scan is never reported as clean.
- **Elements are named, not copied.** A finding names up to five elements as CSS selectors (`#hero > img`). SeoTester
  keeps nothing of a page's body, so there is no HTML excerpt: the agent finds the element in your code from the selector.
- **Website text is untrusted.** Titles, descriptions, URLs and selectors come from the scanned site and reach the agent
  inside a marked data block that tells it to treat them as data. If your site was tampered with, its text should not
  steer the agent into doing something else, but always read what the agent wants to change.
- **Nothing extra to see.** A site or scan you cannot open on the website answers like one that does not exist. Without
  signing in, an agent gets `seotester_explain_check` only; anything else asks it to sign in.
- **Changes are opt-in.** `seotester_start_scan` appears only for an agent you allowed to make changes (at sign-in or on
  Connected applications). Reading never needs it.
- The agent doesn't know which file renders which URL; it searches your repository. Run it from there.

## For developers of SeoTester

- `/mcp` is configured in `Program.cs` (`AddMcp`, `idp.ForMcp()`); the tools are `Mcp/SeoTools.cs`, the prompt
  `Mcp/SeoPrompts.cs`, the tests `SeoTester.Tests/Mcp/SeoToolsTests.cs`.
- Locally the canonical URL is `http://localhost:5014/mcp` (`appsettings.Development.json`): use the `http` address in
  the agent, because Node-based agents do not trust the ASP.NET development certificate.
- Deploying: [deploy.md](deploy.md), "MCP server".
