import angular from '@analogjs/vite-plugin-angular';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [angular()],
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['src/test-setup.ts'],
    include: ['**/*.spec.ts'],
    exclude: ['**/node_modules/**', '**/dist/**', '**/out-tsc/**'],
    coverage: {
      provider: 'v8',
      reporter: ['cobertura', 'text'],
      reportsDirectory: './coverage',
      // Every shipped source counts, not only what some spec happened to
      // import: a file no test touches is 0% covered, not invisible.
      all: true,
      include: ['**/src/**/*.ts'],
      exclude: ['**/*.spec.ts', '**/test-setup.ts', '**/test-utils.ts', '**/public-api.ts', '**/*.d.ts', '**/index.ts', '**/dist/**', '**/node_modules/**'],
    },
  },
});
