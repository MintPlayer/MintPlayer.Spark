import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    coverage: {
      provider: 'v8',
      // lcovonly is what gets uploaded; text-summary is for the person reading the
      // CI log. No html — nothing renders it here.
      reporter: ['cobertura', 'text-summary'],
      reportsDirectory: 'coverage',
      // `all` so untested files count against us. Measuring only what the tests
      // happened to import reports a flattering number that never moves.
      all: true,
      include: ['src/**/*.ts'],
      // dist/ is the committed 2.4 MB ncc bundle of exactly this source — counting
      // it would double every line and drown the real figure.
      exclude: ['src/**/*.test.ts', 'dist/**'],
    },
  },
});
