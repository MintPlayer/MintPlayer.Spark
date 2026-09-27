import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // `describe`/`it`/`expect`/`vi` as globals, matching the rest of this workspace
    // and keeping the test files free of a boilerplate import line each.
    globals: true,
    environment: 'node',
    // bundle.test.ts drives dist/index.js, so it needs a build first and lives
    // behind `npm run test:bundle` instead.
    include: ['src/**/*.test.ts'],
    exclude: ['src/bundle.test.ts'],
    coverage: {
      provider: 'v8',
      // Repo-relative filenames (`apps/CodeCoverage/action/src/...`) rather than
      // `src/...`, a tail far too common to resolve by suffix.
      reporter: [
        ['cobertura', { projectRoot: fileURLToPath(new URL('../../..', import.meta.url)) }],
        'lcovonly',
        'text-summary',
      ],
      // An explicit `include` is what makes a file with no test at all count as
      // uncovered rather than vanish from the denominator. Vitest 4 removed
      // `coverage.all`; this is its replacement, not a companion to it.
      include: ['src/**/*.ts'],
      exclude: ['src/**/*.test.ts', 'dist/**'],
    },
  },
});
