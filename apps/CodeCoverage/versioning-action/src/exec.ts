import { spawn } from 'node:child_process';

export interface ExecResult {
  code: number;
  stdout: string;
  stderr: string;
}

export interface ExecOptions {
  cwd: string;
  env?: NodeJS.ProcessEnv;
}

/** Runs a program without a shell, so no argument is ever parsed as shell syntax. */
export type Exec = (file: string, args: string[], options: ExecOptions) => Promise<ExecResult>;

export const realExec: Exec = (file, args, options) =>
  new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd: options.cwd, env: options.env ?? process.env, shell: false, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (d: Buffer) => (stdout += d.toString('utf8')));
    child.stderr.on('data', (d: Buffer) => (stderr += d.toString('utf8')));
    child.on('error', reject);
    child.on('close', (code) => resolve({ code: code ?? -1, stdout, stderr }));
  });

export async function execOk(exec: Exec, file: string, args: string[], options: ExecOptions): Promise<string> {
  const result = await exec(file, args, options);
  if (result.code !== 0)
    throw new Error(`${file} ${args[0] ?? ''} failed (exit ${result.code}): ${(result.stderr || result.stdout).trim().slice(0, 2000)}`);
  return result.stdout;
}
