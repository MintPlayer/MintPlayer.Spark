# Debugging .NET Applications with Claude Code

## Prerequisites

- [Node.js](https://nodejs.org/) installed (for `npx`)
- Claude Code CLI installed

## Setup

Add the .NET debugger MCP server to Claude Code:

```bash
claude mcp add dotnet-debugger -- npx -y debug-mcp
```

This registers a Model Context Protocol (MCP) server that allows Claude Code to debug .NET applications, attach to running processes, set breakpoints, inspect variables, and discover runtime problems interactively.

## Usage

Once configured, Claude Code can:

- Launch and attach to .NET processes
- Set breakpoints and step through code
- Inspect local variables and evaluate expressions
- Analyze exceptions and stack traces

Simply ask Claude Code to debug your application, e.g.:

> "Debug the Fleet demo app and find out why the API returns a 500 error"

> "Set a breakpoint in DatabaseAccess.cs and inspect the session state"
