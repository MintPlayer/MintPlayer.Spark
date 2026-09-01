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
      reporter: ['lcovonly', 'text-summary'],
      // `all: true` so a file with no test at all still counts as uncovered
      // rather than vanishing from the denominator.
      all: true,
      include: ['src/**/*.ts'],
      exclude: ['src/**/*.test.ts', 'dist/**'],
    },
  },
});
