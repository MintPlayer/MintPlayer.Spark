import { defineConfig } from 'vitest/config';

/**
 * Drives the built `dist/index.js` as a child process, so it needs `npm run
 * build` to have run first. Separate from the default config for that reason:
 * `npm test` must stay runnable on a clean checkout.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/bundle.test.ts'],
    // Each case spawns node and talks to it over a socket.
    testTimeout: 30_000,
    // The stub servers bind real ports; running the files in parallel is fine,
    // but a single file's cases share `stub`, so keep them sequential.
    fileParallelism: false,
  },
});
