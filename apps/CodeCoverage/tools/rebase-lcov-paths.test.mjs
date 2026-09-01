import assert from 'node:assert/strict';
import { test } from 'node:test';
import { rebaseLcov, rebasePath } from './rebase-lcov-paths.mjs';

const ROOT = '/repo';

test('prefixes a project-relative path with its base directory', () => {
  assert.equal(rebasePath('src/main.ts', 'action', ROOT), 'action/src/main.ts');
  assert.equal(
    rebasePath('src/app/spark/row-attr.ts', 'Coverage/ClientApp', ROOT),
    'Coverage/ClientApp/src/app/spark/row-attr.ts',
  );
});

// Vitest emits native separators, so a report produced on Windows arrives with
// backslashes that no POSIX-shaped file list will ever match.
test('normalizes Windows separators to forward slashes', () => {
  assert.equal(rebasePath('src\\app\\spark\\row-attr.ts', 'action', ROOT), 'action/src/app/spark/row-attr.ts');
});

test('strips a leading ./', () => {
  assert.equal(rebasePath('./src/main.ts', 'action', ROOT), 'action/src/main.ts');
});

test('makes an absolute path relative to the repository root', () => {
  assert.equal(rebasePath('/repo/action/src/main.ts', 'action', ROOT), 'action/src/main.ts');
});

test('leaves an already-rebased path alone when the base is the root', () => {
  assert.equal(rebasePath('action/src/main.ts', '.', ROOT), 'action/src/main.ts');
});

// The reason the whole script exists. Both reports say `src/main.ts`; uploaded
// unrebased, each matches two tracked files, and the server drops both without
// saying so.
test('disambiguates the src/main.ts collision between the two workspaces', () => {
  const fromAction = rebasePath('src/main.ts', 'action', ROOT);
  const fromClientApp = rebasePath('src/main.ts', 'Coverage/ClientApp', ROOT);

  assert.notEqual(fromAction, fromClientApp);
  assert.equal(fromAction, 'action/src/main.ts');
  assert.equal(fromClientApp, 'Coverage/ClientApp/src/main.ts');
});

test('rewrites every SF: record and leaves the rest of the report untouched', () => {
  const lcov = [
    'TN:',
    'SF:src\\main.ts',
    'FN:22,collectContext',
    'DA:7,1',
    'end_of_record',
    'TN:',
    'SF:src/status.ts',
    'LF:5',
    'end_of_record',
    '',
  ].join('\n');

  const { contents, paths } = rebaseLcov(lcov, 'action', ROOT);

  assert.deepEqual(paths, ['action/src/main.ts', 'action/src/status.ts']);
  assert.match(contents, /^SF:action\/src\/main\.ts$/m);
  assert.match(contents, /^SF:action\/src\/status\.ts$/m);
  assert.match(contents, /^FN:22,collectContext$/m);
  assert.match(contents, /^DA:7,1$/m);
  assert.equal((contents.match(/end_of_record/g) ?? []).length, 2);
});

test('reports no paths for a report that measured nothing', () => {
  assert.deepEqual(rebaseLcov('TN:\n', 'action', ROOT).paths, []);
});
