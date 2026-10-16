import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    // bundle.test.ts drives dist/index.js (test:bundle); *.integration.test.ts run real package
    // managers against local registries (test:integration, its own CI matrix entry).
    include: ['src/**/*.test.ts'],
    exclude: ['src/bundle.test.ts', 'src/**/*.integration.test.ts'],
    // The orchestrator tests create real git repositories in temp dirs.
    testTimeout: 30_000,
    coverage: {
      provider: 'v8',
      reporter: [
        ['cobertura', { projectRoot: fileURLToPath(new URL('../../..', import.meta.url)) }],
        'lcovonly',
        'text-summary',
      ],
      include: ['src/**/*.ts'],
      exclude: ['src/**/*.test.ts', 'src/index.ts', 'dist/**'],
    },
  },
});
