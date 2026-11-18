import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/**/*.test.ts'],
    coverage: {
      provider: 'v8',
      // Repo-relative filenames, as the other workspace packages report them.
      reporter: [['cobertura', { projectRoot: fileURLToPath(new URL('../../..', import.meta.url)) }], 'lcovonly', 'text-summary'],
      include: ['src/**/*.ts'],
      exclude: ['src/**/*.test.ts', 'src/testing/**', 'dist/**'],
    },
  },
});
