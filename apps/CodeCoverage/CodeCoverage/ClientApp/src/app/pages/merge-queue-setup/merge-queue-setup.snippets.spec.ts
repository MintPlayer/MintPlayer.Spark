import { describe, expect, it } from 'vitest';
import { COVERAGE_YML, LOCKFILES_WORKFLOW, REGENERATE_WORKFLOW } from './merge-queue-setup.snippets';

/*
 * Two of the page's snippets are copies of files elsewhere in the repository (merge-queue-setup.snippets.ts). These
 * cases read the real files, so a change on either side that the other does not follow fails here.
 *
 * How the files are reached, as measured under this project's runner (@nx/angular:unit-test with vitest):
 * - Node's fs and path are imported at run time. The test bundle is built for the browser, where esbuild refuses a
 *   static `node:fs` import, and the spec program has no Node types. Vitest itself runs in Node, so this import works.
 * - Paths start from this spec file as vitest reports it (`testPath`). `import.meta.url` is not this file once coverage
 *   is on, as CI runs it: the Angular builder then serves the bundle as `<workspace root>/spec-<name>.js`.
 * - Both files lie outside this project, so project.json lists them as inputs of its `test` target. Without that, a
 *   change to only one of them would replay a cached pass instead of running these cases.
 */

interface NodeFs { readFileSync(path: string, encoding: 'utf8'): string }
interface NodePath { dirname(path: string): string; resolve(...segments: string[]): string }
const nodeModule = <T>(name: string): Promise<T> => import(/* @vite-ignore */ name);

/**
 * A file of the repository, by its path from the root. CRLF reads as LF: with `core.autocrlf`, a Windows checkout has
 * CRLF, while the snippets, being template literals, always have LF.
 */
async function repositoryFile(pathFromRoot: string): Promise<string> {
  const [fs, path] = await Promise.all([nodeModule<NodeFs>('node:fs'), nodeModule<NodePath>('node:path')]);
  // This spec lies eight folders deep: apps/CodeCoverage/CodeCoverage/ClientApp/src/app/pages/merge-queue-setup/.
  const root = path.resolve(path.dirname(expect.getState().testPath!), '../../../../../../../..');
  return fs.readFileSync(path.resolve(root, pathFromRoot), 'utf8').replace(/\r\n/g, '\n');
}

/** The `mergeQueue:` section of a coverage.yml: from its key up to the next top-level key, or the end of the file. */
function mergeQueueSection(yml: string): string {
  const lines = yml.split('\n');
  const start = lines.findIndex(line => line.startsWith('mergeQueue:'));
  if (start < 0) throw new Error('coverage.yml has no top-level mergeQueue: key');
  const next = lines.findIndex((line, i) => i > start && /^[^\s#]/.test(line));
  return lines.slice(start, next < 0 ? lines.length : next).join('\n');
}

/**
 * YAML without its comments: comment lines go, and so does a trailing comment, which YAML starts at a `#` after
 * whitespace and outside quotes. Every key, value, indent and line order stays as written.
 */
function withoutComments(yml: string): string {
  return yml.split('\n')
    .filter(line => !line.trimStart().startsWith('#'))
    .map(withoutTrailingComment)
    .join('\n')
    .trimEnd();
}

function withoutTrailingComment(line: string): string {
  let quote: string | undefined;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (quote) {
      if (quote === '"' && c === '\\') i++;
      else if (c === quote) quote = undefined;
    } else if (c === '"' || c === '\'') {
      quote = c;
    } else if (c === '#' && /\s/.test(line[i - 1] ?? ' ')) {
      return line.slice(0, i).trimEnd();
    }
  }
  return line.trimEnd();
}

describe('the setup page\'s copied snippets', () => {
  it('show docs/code-coverage/templates/regenerate.yml byte for byte', async () => {
    // The snippet leaves out the file's final newline, so the code block ends on its last line.
    expect(REGENERATE_WORKFLOW + '\n').toBe(await repositoryFile('docs/code-coverage/templates/regenerate.yml'));
  });

  it('show docs/code-coverage/templates/lockfiles-regenerate.yml byte for byte', async () => {
    expect(LOCKFILES_WORKFLOW + '\n').toBe(await repositoryFile('docs/code-coverage/templates/lockfiles-regenerate.yml'));
  });

  // Compared without comments: the page's comments leave out the plan's decision numbers on purpose, and no comment
  // changes what the queue reads. Every key, value, indent and line order must match, so a change to any of them, on
  // either side, fails.
  it('show the mergeQueue: section of the repository\'s coverage.yml, with the same keys and values', async () => {
    const root = mergeQueueSection(await repositoryFile('coverage.yml'));

    expect(withoutComments(COVERAGE_YML)).toBe(withoutComments(root));
  });

  it('compare coverage.yml by everything but its comments', () => {
    const yml = [
      '# The policy.',
      'mergeQueue:',
      '  # The label.',
      '  enqueue:',
      '    label: merge-queue  # trailing',
      '  regeneratedPaths:',
      '    - "a #b"',
      '    - \'c #d\' # trailing',
      'other: 1',
    ].join('\n');

    expect(withoutComments(mergeQueueSection(yml))).toBe([
      'mergeQueue:',
      '  enqueue:',
      '    label: merge-queue',
      '  regeneratedPaths:',
      '    - "a #b"',
      '    - \'c #d\'',
    ].join('\n'));
    expect(withoutComments(mergeQueueSection(yml.replace('label: merge-queue', 'label: queue'))))
      .not.toBe(withoutComments(mergeQueueSection(yml)));
  });
});
