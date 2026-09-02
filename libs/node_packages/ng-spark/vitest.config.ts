import angular from '@analogjs/vite-plugin-angular';
import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [angular()],
  resolve: {
    alias: [
      // Mirror tsconfig.base.json paths so source files can use the package-style imports
      // they ship with (e.g. `@mintplayer/ng-spark/services`).
      { find: /^@mintplayer\/ng-spark\/(.+)$/, replacement: path.join(root, '$1', 'index.ts') },
      { find: '@mintplayer/ng-spark', replacement: path.join(root, 'src/public-api.ts') },
    ],
  },
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
      exclude: ['**/*.spec.ts', '**/test-setup.ts', '**/public-api.ts', '**/*.d.ts', '**/index.ts', '**/dist/**', '**/node_modules/**'],
    },
  },
});
