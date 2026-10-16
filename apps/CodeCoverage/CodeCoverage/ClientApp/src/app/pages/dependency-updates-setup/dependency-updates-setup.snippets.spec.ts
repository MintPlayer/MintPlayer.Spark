import { describe, expect, it } from 'vitest';
import { DEPENDENCIES_YML } from './dependency-updates-setup.snippets';

/*
 * The page's `dependencies:` snippet is a copy of the user guide's (docs/code-coverage/dependency-updates.md §4). This
 * case reads the guide itself, so a key added or changed on one side and not the other fails here. The guide is reached
 * the way merge-queue-setup.snippets.spec.ts reaches its files (see there for why): Node's fs at run time, from this
 * spec's own path, and project.json lists the guide as an input of the `test` target so a change to it alone re-runs.
 */

interface NodeFs { readFileSync(path: string, encoding: 'utf8'): string }
interface NodePath { dirname(path: string): string; resolve(...segments: string[]): string }
const nodeModule = <T>(name: string): Promise<T> => import(/* @vite-ignore */ name);

/** A file of the repository, by its path from the root, with CRLF read as LF. */
async function repositoryFile(pathFromRoot: string): Promise<string> {
  const [fs, path] = await Promise.all([nodeModule<NodeFs>('node:fs'), nodeModule<NodePath>('node:path')]);
  // This spec lies eight folders deep: apps/CodeCoverage/CodeCoverage/ClientApp/src/app/pages/dependency-updates-setup/.
  const root = path.resolve(path.dirname(expect.getState().testPath!), '../../../../../../../..');
  return fs.readFileSync(path.resolve(root, pathFromRoot), 'utf8').replace(/\r\n/g, '\n');
}

/** The body of the first fenced `yaml` block after the heading that starts with `heading`. */
function yamlBlockAfter(markdown: string, heading: string): string {
  const lines = markdown.split('\n');
  const at = lines.findIndex(line => line.startsWith(heading));
  if (at < 0) throw new Error(`no heading ${heading}`);
  const open = lines.findIndex((line, i) => i > at && line === '```yaml');
  const close = lines.findIndex((line, i) => i > open && line === '```');
  if (open < 0 || close < 0) throw new Error(`no yaml block after ${heading}`);
  return lines.slice(open + 1, close).join('\n');
}

describe('the dependency updater setup page\'s copied snippet', () => {
  it('shows the yaml block of docs/code-coverage/dependency-updates.md §4 byte for byte', async () => {
    const guide = await repositoryFile('docs/code-coverage/dependency-updates.md');

    expect(DEPENDENCIES_YML).toBe(yamlBlockAfter(guide, '## 4.'));
  });
});
