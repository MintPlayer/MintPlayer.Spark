import { execOk, type Exec } from './exec';

const BRANCH = /^(?!-)(?!.*\.\.)[A-Za-z0-9._\/-]{1,255}$/;

export function checkBranchName(name: string): string {
  if (!BRANCH.test(name)) throw new Error(`'${name}' is not a branch name the action accepts.`);
  return name;
}

/**
 * Gives the engine the history it reads (PRD D3): every tag, the commits back to them, and the remote-tracking refs of
 * the built branch (a tag is pushed only for a commit on it) and the default branch (whose coverage.yml configures the
 * run, as on the server). A shallow checkout, actions/checkout's default, is deepened to the full history.
 */
export async function prepareHistory(exec: Exec, cwd: string, branch: string, defaultBranch: string): Promise<void> {
  const shallow = (await execOk(exec, 'git', ['rev-parse', '--is-shallow-repository'], { cwd })).trim() === 'true';
  const refs = [...new Set([branch, defaultBranch])].map((b) => `+refs/heads/${checkBranchName(b)}:refs/remotes/origin/${b}`);
  await execOk(exec, 'git', ['fetch', '--no-recurse-submodules', '--tags', '--force', ...(shallow ? ['--unshallow'] : []), 'origin', ...refs], { cwd });
}

/** A file on the default branch, or null when it has none. */
export async function showOnDefaultBranch(exec: Exec, cwd: string, defaultBranch: string, path: string): Promise<string | null> {
  const result = await exec('git', ['show', `refs/remotes/origin/${checkBranchName(defaultBranch)}:${path}`], { cwd });
  return result.code === 0 ? result.stdout : null;
}

/** Every package's tagged versions, from the `<id>/v<version>` tags (PRD D9). */
export async function taggedVersions(exec: Exec, cwd: string): Promise<Map<string, string[]>> {
  const out = await execOk(exec, 'git', ['tag', '--list'], { cwd });
  const byId = new Map<string, string[]>();
  for (const tag of out.split('\n').map((t) => t.trim())) {
    const at = tag.lastIndexOf('/v');
    if (at <= 0) continue;
    const id = tag.slice(0, at);
    byId.set(id, [...(byId.get(id) ?? []), tag.slice(at + 2)]);
  }
  return byId;
}
