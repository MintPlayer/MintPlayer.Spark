import * as glob from '@actions/glob';
import * as path from 'path';

// Well-known coverage file names/patterns (subset of Codecov's proven list).
const DEFAULT_PATTERNS = [
  '**/lcov.info',
  '**/*.lcov',
  '**/coverage.xml',
  '**/cobertura.xml',
  '**/cobertura-coverage.xml',
  '**/coverage.cobertura.xml',
  '**/clover.xml',
  '**/jacoco*.xml',
  '**/coverage-final.json',
];

// Folders that only ever contain other people's coverage files.
const IGNORED_DIRS = [
  '**/node_modules/**',
  '**/bower_components/**',
  '**/vendor/**',
  '**/.git/**',
  '**/.tox/**',
  '**/.venv/**',
  '**/venv/**',
  '**/__pycache__/**',
  '**/htmlcov/**',
  '**/.nyc_output/**',
  '**/jspm_packages/**',
];

export async function findCoverageFiles(
  explicit: string | undefined,
  directory: string | undefined,
  rootDir: string,
  disableSearch: boolean,
): Promise<string[]> {
  const results = new Set<string>();

  if (explicit) {
    const patterns = explicit
      .split(/[\n,]/)
      .map((p) => p.trim())
      .filter((p) => p.length > 0);
    const globber = await glob.create(patterns.join('\n'), { followSymbolicLinks: false });
    for (const file of await globber.glob()) {
      results.add(file);
    }
  }

  if (!disableSearch && results.size === 0) {
    const base = directory ? path.resolve(rootDir, directory) : rootDir;
    const patterns = DEFAULT_PATTERNS.map((p) => path.join(base, p)).concat(
      IGNORED_DIRS.map((d) => '!' + path.join(base, d)),
    );
    const globber = await glob.create(patterns.join('\n'), { followSymbolicLinks: false });
    for (const file of await globber.glob()) {
      results.add(file);
    }
  }

  return [...results];
}
