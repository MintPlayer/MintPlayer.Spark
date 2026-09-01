#!/usr/bin/env node
/**
 * NOT CURRENTLY WIRED INTO CI IN THIS REPOSITORY, and deliberately so.
 *
 * It was load-bearing in the standalone MintPlayer/CodeCoverage repo, where the action
 * and the SPA both emitted lcov and both reported `src/main.ts`. Two things removed that
 * collision: the SPA switched to cobertura, matching what ng-spark and ng-spark-auth
 * already emitted, and the action left this repository altogether for
 * MintPlayer/github-actions. A vitest cobertura report
 * carries an absolute `<source>` root, so every path is unambiguous by construction and
 * there is nothing left to rebase — a better fix than rebasing, because it removes the
 * failure mode rather than detecting it.
 *
 * Kept because it is the only thing that would make lcov safe here again if a project
 * ever switches back, and its `node --test` suite still passes. Delete it if that stops
 * being a plausible future.
 *
 * ---
 *
 * Rewrites the `SF:` paths in an lcov report so they are relative to the repository
 * root, and verifies that every one of them names a tracked file.
 *
 *   node tools/rebase-lcov-paths.mjs <lcov-file> <base-dir>
 *
 * `base-dir` is where the reported paths are relative to, as a repo-root-relative
 * path — `action` or `Coverage/ClientApp`.
 *
 * Why this exists: the ingestion server resolves report paths against the
 * repository's file list by longest matching suffix, and **silently drops whatever
 * is ambiguous**. Vitest reports paths relative to its own project root, so this
 * repository emits `src/main.ts` from both `action/` and `Coverage/ClientApp/`.
 * Uploaded as-is, each matches two tracked files, both are dropped, and the report
 * arrives smaller with no error anywhere — which reads as a coverage drop rather
 * than a bug. Rebasing makes every path unique and exact.
 *
 * The verification is the point as much as the rewrite: an unmatched path is a
 * file the server would drop, so this fails loudly rather than uploading a report
 * that quietly under-counts.
 */
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

/** Repo-root-relative, forward slashes, no `./` — the shape `git ls-files` prints. */
export function rebasePath(reported, baseDir, repoRoot = process.cwd()) {
  const normalized = reported.replace(/\\/g, '/').trim();

  const absolute = path.isAbsolute(normalized) || /^[a-zA-Z]:\//.test(normalized);
  const relative = absolute
    ? path.relative(repoRoot, normalized)
    : path.join(baseDir, normalized.replace(/^\.\//, ''));

  return relative.split(path.sep).join('/');
}

export function rebaseLcov(contents, baseDir, repoRoot = process.cwd()) {
  const paths = [];
  const rewritten = contents.replace(/^SF:(.*)$/gm, (_, reported) => {
    const rebased = rebasePath(reported, baseDir, repoRoot);
    paths.push(rebased);
    return `SF:${rebased}`;
  });
  return { contents: rewritten, paths };
}

function trackedFiles(repoRoot) {
  const output = execFileSync('git', ['ls-files'], { cwd: repoRoot, encoding: 'utf8' });
  return new Set(output.split('\n').filter(Boolean));
}

function main(argv) {
  const [file, baseDir] = argv;
  if (!file || !baseDir) {
    console.error('usage: node tools/rebase-lcov-paths.mjs <lcov-file> <base-dir>');
    return 2;
  }

  const repoRoot = process.cwd();
  const { contents, paths } = rebaseLcov(readFileSync(file, 'utf8'), baseDir, repoRoot);

  if (paths.length === 0) {
    console.error(`::error::${file} contains no SF: records — nothing was measured.`);
    return 1;
  }

  const tracked = trackedFiles(repoRoot);
  const unmatched = paths.filter((p) => !tracked.has(p));
  if (unmatched.length > 0) {
    console.error(
      `::error::${unmatched.length} of ${paths.length} paths in ${file} name no tracked file. ` +
        `The server would drop them silently. First few:`,
    );
    for (const p of unmatched.slice(0, 10)) console.error(`  ${p}`);
    return 1;
  }

  writeFileSync(file, contents);
  console.log(`Rebased ${paths.length} path(s) in ${file} onto ${baseDir}/.`);
  return 0;
}

// Importable for the tests; only the CLI invocation touches the filesystem.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}
