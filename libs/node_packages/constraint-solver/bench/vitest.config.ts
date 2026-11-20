import { defineConfig } from 'vitest/config';

// The drag benchmark only (npm run bench); the unit tests use ../vitest.config.ts.
export default defineConfig({
  test: { globals: true, environment: 'node', include: ['bench/**/*.bench.ts']},
});
