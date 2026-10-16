import { spawn } from 'node:child_process';

export interface ExecResult {
  code: number;
  stdout: string;
  stderr: string;
}

export interface ExecOptions {
  cwd: string;
  env?: Record<string, string | undefined>;
  /** Kill after this many ms (the job's own timeout-minutes is the outer bound). */
  timeoutMs?: number;
}

/**
 * Runs a program WITHOUT a shell: every argument is passed as-is, so a plan value can never become
 * shell syntax. Never throws for a non-zero exit; callers branch on `code`.
 */
export type Exec = (command: string, args: readonly string[], options: ExecOptions) => Promise<ExecResult>;

const MAX_CAPTURE = 4 * 1024 * 1024;

export const realExec: Exec = (command, args, options) =>
  new Promise((resolve) => {
    const child = spawn(command, [...args], {
      cwd: options.cwd,
      env: { ...process.env, ...options.env } as NodeJS.ProcessEnv,
      shell: false,
      windowsHide: true,
      // npm, npx, pnpm, yarn are .cmd shims on Windows; spawn without a shell cannot run them.
      // CI is Linux; this only matters for local runs of the integration tests.
      ...(process.platform === 'win32' ? { shell: needsWindowsShim(command) } : {}),
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d: Buffer) => {
      if (stdout.length < MAX_CAPTURE) stdout += d.toString('utf8');
    });
    child.stderr.on('data', (d: Buffer) => {
      if (stderr.length < MAX_CAPTURE) stderr += d.toString('utf8');
    });
    let timer: NodeJS.Timeout | undefined;
    if (options.timeoutMs) {
      timer = setTimeout(() => child.kill('SIGKILL'), options.timeoutMs);
    }
    child.on('error', (err) => {
      if (timer) clearTimeout(timer);
      resolve({ code: 127, stdout, stderr: stderr + String(err.message) });
    });
    child.on('close', (code) => {
      if (timer) clearTimeout(timer);
      resolve({ code: code ?? 1, stdout, stderr });
    });
  });

function needsWindowsShim(command: string): boolean {
  return ['npm', 'npx', 'pnpm', 'yarn', 'corepack'].includes(command);
}

/** True when `command` can be started (used to fail a group with "tool missing" instead of crashing). */
export async function hasTool(exec: Exec, command: string, cwd: string): Promise<boolean> {
  const probe = command === 'go' ? ['version'] : ['--version'];
  const result = await exec(command, probe, { cwd, timeoutMs: 60_000 });
  return result.code !== 127;
}

export function combined(result: ExecResult): string {
  return `${result.stdout}\n${result.stderr}`.trim();
}
