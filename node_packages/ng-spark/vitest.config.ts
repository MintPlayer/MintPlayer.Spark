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
  },
});
