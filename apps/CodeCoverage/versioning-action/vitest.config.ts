import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/**/*.test.ts'],
    // The git tests create real repositories in temp dirs.
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
