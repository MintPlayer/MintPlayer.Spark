import { defineConfig } from 'vitest/config';

/**
 * Real package managers against LOCAL registries (verdaccio, a NuGet folder feed, --find-links, a
 * file:// Maven repo, GOPROXY=file://, a Cargo local registry, a Composer artifact repo). Public
 * registries are unset so an accidental fetch fails instead of passing.
 *
 * DEP_ACTION_IT_GROUP selects a CI matrix slice: one of node+dotnet, jvm, python+go+rust+php,
 * docker+renovate, or a comma-separated list of them. Unset = every group (a local run). Tests whose tool is missing skip LOUDLY
 * (the skip reason names the tool), and Docker tests skip on Windows.
 */
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/**/*.integration.test.ts'],
    testTimeout: 300_000,
    hookTimeout: 300_000,
    fileParallelism: false,
  },
});
