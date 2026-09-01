import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { findCoverageFiles } from './files';

let root: string;

/** Writes `relative` (creating parents) so the globber has something real to find. */
function write(relative: string): string {
  const absolute = path.join(root, relative);
  fs.mkdirSync(path.dirname(absolute), { recursive: true });
  fs.writeFileSync(absolute, 'TN:\n');
  return absolute;
}

/** Compare as workspace-relative POSIX paths — @actions/glob returns absolute. */
function relative(files: string[]): string[] {
  return files.map((f) => path.relative(root, f).split(path.sep).join('/')).sort();
}

beforeEach(() => {
  root = fs.mkdtempSync(path.join(os.tmpdir(), 'coverage-files-'));
});

afterEach(() => {
  fs.rmSync(root, { recursive: true, force: true });
});

describe('findCoverageFiles', () => {
  it('auto-detects well-known report names when no files are given', async () => {
    write('ClientApp/coverage/lcov.info');
    write('coverage/abc-guid/coverage.cobertura.xml');
    write('src/notes.txt');

    const found = await findCoverageFiles(undefined, undefined, root, false);

    expect(relative(found)).toEqual([
      'ClientApp/coverage/lcov.info',
      'coverage/abc-guid/coverage.cobertura.xml',
    ]);
  });

  it('resolves an explicit glob', async () => {
    write('coverage/one/coverage.cobertura.xml');
    write('coverage/two/coverage.cobertura.xml');

    const found = await findCoverageFiles(
      path.join(root, 'coverage/**/coverage.cobertura.xml'),
      undefined,
      root,
      true,
    );

    expect(relative(found)).toEqual([
      'coverage/one/coverage.cobertura.xml',
      'coverage/two/coverage.cobertura.xml',
    ]);
  });

  it('accepts several patterns separated by newlines or commas', async () => {
    write('action/coverage/lcov.info');
    write('ClientApp/coverage/lcov.info');
    write('coverage/guid/coverage.cobertura.xml');

    const newlines = await findCoverageFiles(
      `${path.join(root, 'action/coverage/lcov.info')}\n${path.join(root, 'ClientApp/coverage/lcov.info')}`,
      undefined,
      root,
      true,
    );
    const commas = await findCoverageFiles(
      `${path.join(root, 'action/coverage/lcov.info')},${path.join(root, 'ClientApp/coverage/lcov.info')}`,
      undefined,
      root,
      true,
    );

    expect(relative(newlines)).toEqual(['ClientApp/coverage/lcov.info', 'action/coverage/lcov.info']);
    expect(relative(commas)).toEqual(relative(newlines));
  });

  // Somebody else's coverage report, vendored into node_modules, is not ours.
  it('ignores vendored directories while auto-detecting', async () => {
    write('node_modules/some-package/coverage/lcov.info');
    write('vendor/other/lcov.info');
    write('coverage/lcov.info');

    const found = await findCoverageFiles(undefined, undefined, root, false);

    expect(relative(found)).toEqual(['coverage/lcov.info']);
  });

  it('scopes auto-detection to `directory` when one is given', async () => {
    write('action/coverage/lcov.info');
    write('ClientApp/coverage/lcov.info');

    const found = await findCoverageFiles(undefined, 'action', root, false);

    expect(relative(found)).toEqual(['action/coverage/lcov.info']);
  });

  // This is why every consuming workflow passes disable-search: true. Without it
  // a glob typo silently becomes a whole-workspace sweep that picks up formats
  // the server has no parser for.
  it('falls back to auto-detection when an explicit glob matches nothing', async () => {
    write('coverage/lcov.info');

    const found = await findCoverageFiles(path.join(root, 'nowhere/**/*.xml'), undefined, root, false);

    expect(relative(found)).toEqual(['coverage/lcov.info']);
  });

  it('returns nothing rather than falling back when search is disabled', async () => {
    write('coverage/lcov.info');

    const found = await findCoverageFiles(path.join(root, 'nowhere/**/*.xml'), undefined, root, true);

    expect(found).toEqual([]);
  });

  it('does not fall back when the explicit glob already matched', async () => {
    write('coverage/guid/coverage.cobertura.xml');
    write('ClientApp/coverage/lcov.info');

    const found = await findCoverageFiles(
      path.join(root, 'coverage/**/coverage.cobertura.xml'),
      undefined,
      root,
      false,
    );

    expect(relative(found)).toEqual(['coverage/guid/coverage.cobertura.xml']);
  });

  it('de-duplicates a file matched by two patterns', async () => {
    write('coverage/lcov.info');
    const both = `${path.join(root, 'coverage/lcov.info')}\n${path.join(root, 'coverage/*.info')}`;

    const found = await findCoverageFiles(both, undefined, root, true);

    expect(found).toHaveLength(1);
  });
});
