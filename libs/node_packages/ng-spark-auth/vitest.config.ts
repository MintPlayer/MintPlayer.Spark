import angular from '@analogjs/vite-plugin-angular';
import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  plugins: [angular()],
  resolve: {
    alias: {
      // Mirrors tsconfig.base.json. Without it `@mintplayer/ng-spark` does not resolve
      // here, and the cost was not a failing test — it was a SILENT hole in the
      // measurement: provide-spark-auth.ts imports it, so vitest could not transform the
      // file, fell back to parsing the TypeScript as raw JavaScript, failed on
      // `config?: Partial<SparkAuthConfig>`, logged "Excluding it from coverage" and
      // carried on. The file disappeared from the denominator rather than reporting 0%,
      // which is why this package looked like 20 source files when it has 21.
      '@mintplayer/ng-spark': fileURLToPath(new URL('../ng-spark/src/public-api.ts', import.meta.url)),
    },
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
      //
      // `include` is what does that. Vitest 4 REMOVED `coverage.all` -- setting an
      // explicit `include` is the replacement ("by default only files covered by
      // tests are included"). An `all: true` here was dead config that also failed
      // `tsc --noEmit`, and it was easy to mistake for the thing making this work.
      include: ['**/src/**/*.ts'],
      exclude: ['**/*.spec.ts', '**/test-setup.ts', '**/test-utils.ts', '**/public-api.ts', '**/*.d.ts', '**/index.ts', '**/dist/**', '**/node_modules/**'],
    },
  },
});
