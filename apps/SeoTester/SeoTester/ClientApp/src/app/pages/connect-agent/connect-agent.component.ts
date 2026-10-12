import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

type ClientKey = 'claude' | 'codex' | 'gemini' | 'vscode' | 'other';

/** One step of a client's instructions: a translated sentence, optionally followed by a copyable command. */
interface Step { text: string; code?: string; }

/**
 * "Connect an AI agent" (/connect-agent): how to add SeoTester's MCP server (/mcp) to Claude Code, Codex, Gemini CLI,
 * VS Code or any other MCP client. The source material is docs/guide-mcp-agents.md and docs/seotester/mcp.md.
 *
 * The address comes from the page's own origin, never a hard-coded production URL, so a developer's machine shows
 * localhost. Static content only: anonymous visitors see it too, before they have an account.
 */
@Component({
  selector: 'app-connect-agent',
  imports: [NgTemplateOutlet, RouterLink, BsCodeSnippetComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, TranslateKeyPipe],
  templateUrl: './connect-agent.component.html',
  styles: [`
    .agent-page { max-width: 52rem; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class ConnectAgentComponent {
  /** The name the agent knows the server by (the tools carry their own `seotester_` prefix). */
  protected readonly serverName = 'seotester';
  protected readonly mcpUrl = inject(DOCUMENT).location.origin + '/mcp';

  protected readonly clients: ClientKey[] = ['claude', 'codex', 'gemini', 'vscode', 'other'];

  protected readonly steps: Record<ClientKey, Step[]> = {
    claude: [
      { text: 'mcpUi.claude.add', code: `claude mcp add --transport http ${this.serverName} ${this.mcpUrl}` },
      { text: 'mcpUi.claude.auth', code: '/mcp' },
    ],
    codex: [
      { text: 'mcpUi.codex.add', code: `codex mcp add ${this.serverName} --url ${this.mcpUrl}` },
      { text: 'mcpUi.codex.login', code: `codex mcp login ${this.serverName}` },
    ],
    gemini: [
      { text: 'mcpUi.gemini.add', code: `gemini mcp add --transport http ${this.serverName} ${this.mcpUrl}` },
      { text: 'mcpUi.gemini.auth', code: `/mcp auth ${this.serverName}` },
    ],
    vscode: [
      { text: 'mcpUi.vscode.command', code: this.mcpUrl },
      { text: 'mcpUi.vscode.file', code: JSON.stringify({ servers: { [this.serverName]: { type: 'http', url: this.mcpUrl } } }, null, 2) },
    ],
    other: [
      { text: 'mcpUi.other.oauth', code: this.mcpUrl },
      { text: 'mcpUi.other.token', code: 'Authorization: Bearer spat_…' },
    ],
  };

  protected readonly examples = ['mcpUi.example.fix', 'mcpUi.example.list', 'mcpUi.example.explain'];
}
