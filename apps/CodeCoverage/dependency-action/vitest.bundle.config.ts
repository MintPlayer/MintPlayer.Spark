import { defineConfig } from 'vitest/config';

/**
 * Drives the committed `dist/index.js` as a child process (the file `runs.main` executes), so a
 * bundle built from the wrong entry point, or not rebuilt, fails here and nowhere else.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/bundle.test.ts'],
    testTimeout: 60_000,
    fileParallelism: false,
  },
});
