# Connect an AI agent to a Spark app

> **Draft.** Describes the planned feature in [spark_mcp_PRD.md](spark_mcp_PRD.md). Commands were taken
> from each agent's documentation on 2026-10-12 and still have to be run against a live server.

Some Spark apps, such as [SeoTester](seotester/mcp.md) and [CodeCoverage](code-coverage/mcp.md), let you
connect an AI coding agent: Claude Code, OpenAI Codex or Gemini. The agent can then answer questions and
do work with your data in that app: "which SEO errors does my site have, and fix them", "where can I
raise test coverage most cheaply", "give me a list of all contracts".

**The agent works as you.** You sign in once in your browser, and the agent only sees what you see in the
app. It never gets your password.

## What you need

- The app's MCP address, shown on your account page in the app, for example
  `https://seotester.mintplayer.com/mcp`.
- An account in that app.
- One of the agents below.

## Add the server to your agent

| Agent | Minimum version | How to sign in |
|---|---|---|
| Claude Code | 2.1.296 (tested) | `claude mcp login <name>` in a terminal, or `/mcp` → **Authenticate** in a session |
| OpenAI Codex CLI | **0.148** (first version with client metadata documents; older versions fall back to dynamic registration) | `codex mcp add <name> --url …` signs in automatically; later `codex mcp login <name>` |
| Gemini CLI | 0.63 | `/mcp auth <name>` in a session |
| VS Code (GitHub Copilot agent mode) | current (sign-in path read from its source, not yet tried live) | VS Code asks to sign in when the server requires it |

**Sign in before you need it.** None of the agents opens the browser by itself halfway through a
session. If the agent connected without signing in and then reports that sign-in is required, run the
sign-in command from the table yourself (Codex: then reconnect, for example by restarting the session).
Signing in to the app's website in your browser does not sign the agent in.

### Claude Code

```powershell
claude mcp add --transport http seotester https://seotester.mintplayer.com/mcp
claude mcp login seotester
```

Add `--scope project` to share it with your team through `.mcp.json` in the repository (only the address
is stored there, never a token). Instead of `claude mcp login`, you can sign in from a Claude Code
session:

```
/mcp
```

Select the server and choose **Authenticate**. Your browser opens; sign in to the app and approve.

### OpenAI Codex CLI

Version 0.148 or later.

```powershell
codex mcp add seotester --url https://seotester.mintplayer.com/mcp
```

`codex mcp add` opens the browser to sign in right away. To sign in again later:

```powershell
codex mcp login seotester
```

Or in `~/.codex/config.toml` (then run `codex mcp login seotester`):

```toml
[mcp_servers.seotester]
url = "https://seotester.mintplayer.com/mcp"
```

### Gemini CLI / Gemini Code Assist

In `~/.gemini/settings.json` (or `.gemini/settings.json` in the project):

```json
{
  "mcpServers": {
    "seotester": { "httpUrl": "https://seotester.mintplayer.com/mcp" }
  }
}
```

Then, in a session: `/mcp auth seotester`. Gemini CLI 0.63 or later.

### VS Code (GitHub Copilot agent mode)

In `.vscode/mcp.json` in the project (or through the **MCP: Add Server** command):

```json
{
  "servers": {
    "seotester": { "type": "http", "url": "https://seotester.mintplayer.com/mcp" }
  }
}
```

When the server asks for sign-in, VS Code offers to sign you in. It opens your browser, and after you
approve, the browser may pass through `vscode.dev/redirect` on its way back to VS Code. That page belongs
to VS Code; the app accepts it for VS Code only.

## Signing in

Without signing in, the agent sees only what the app shows to anonymous visitors. Some apps require
sign-in for everything; there the agent signs you in as soon as it connects. Elsewhere, sign in with the
command for your agent (table above) when it needs more:

1. Your browser opens.
2. Sign in the way you always do in that app (GitHub, Microsoft, passkey...).
3. The app shows which agent asks for access, whether it could verify the agent (an agent marked
   **unverified** may not be who it says it is), and lets you choose:
   - **What it may see**: everything the app makes available to agents, or only the parts you tick.
   - **Allow changes**: off by default, and only offered if the app permits it for this agent. Even when
     on, every change is shown to you first and needs your approval in the agent.
4. Approve. The browser says you can close it, and the agent continues.

The agent keeps you signed in. You won't see the browser again unless you disconnect it or your access
expires. You can change your choices later without signing in again (see below).

## Ask away

Examples:

- "List my sites in SeoTester and the critical findings of the latest scan."
- "Fix the missing meta descriptions SeoTester found, then start a new scan to check."
- "Using CodeCoverage, which files in this repository would raise coverage most with the least effort?
  Write tests for the top three."
- "Give me a list of all contracts that end this year."

## What the agent can and cannot do

- It sees only what your account sees, page for page and row for row.
- It sees only the parts you allowed it, even if your account sees more.
- It cannot change anything unless you allowed changes, and then only after showing you the change and
  getting your approval. Without signing in it can't change anything at all.
- Text from other people or from websites (page titles, comments) is marked as data for the agent. Still,
  read what your agent proposes before you approve it.

## Change or disconnect an agent

Open your **account page → Connected applications** in the app. Per agent you see when it was last used
and can:

- change what it may see, or turn changes on or off: this applies to its next request;
- **Disconnect** it: its access stops immediately.

Changing your password or removing a sign-in method disconnects all agents. An agent that signs in at another
server (your organization's, or another app's sign-in) then shows as **disconnected**: edit it to allow it again.

## Agents without a browser (CI, scheduled jobs)

Create a **personal access token** on the same account page, choosing what it may see and whether it may
make changes, and how many days it stays valid (90 unless you change it, at most 365). It starts with
`spat_` and is shown once: copy it right away. Store it as a secret, never in a repository. The token
works only for the agent connection (the MCP address), not for the website or its API. Then:

- Claude Code: `claude mcp add --transport http seotester https://seotester.mintplayer.com/mcp --header "Authorization: Bearer ${SPARK_PAT}"`
- Codex: `bearer_token_env_var = "SPARK_PAT"` under `[mcp_servers.seotester]`
- Gemini: `"headers": { "Authorization": "Bearer $SPARK_PAT" }`

A configured token replaces the browser sign-in, so don't set one if you want to sign in interactively.

## Troubleshooting

| Problem | Try |
|---|---|
| The browser never opens | Remove any `Authorization` header from the agent's configuration, then authenticate again. |
| "Not found" for something you can see in the app | You excluded it for this agent (check Connected applications), or the app doesn't make that data available to agents. |
| The browser never opens, but answers are incomplete | You're not signed in, so the agent sees only public data, and agents don't start sign-in by themselves mid-session. In Claude Code run `claude mcp login <name>` or `/mcp` → Authenticate; in Codex `codex mcp login <name>`, then reconnect; in Gemini `/mcp auth <name>`. |
| You signed in on the website, the agent still isn't | The website sign-in is for your browser only. Sign the agent in with its own command (see above). |
| The agent says it isn't allowed to change data | Turn on changes for it under Connected applications. If the option isn't offered, the app doesn't allow changes by this agent. |
| Everything worked yesterday, now 401 | Your access was revoked (password change, disconnect). Authenticate again. |
