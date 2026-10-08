// PWA guard (#464, D8): every Spark app that ships Angular's service worker keeps the server's
// own URLs out of it.
//
// The service worker answers every navigation it matches with the cached index.html. A navigation
// to /spark/auth/external-login, /signin-github, /connect/authorize or /.well-known/* that the
// worker answered would never reach ASP.NET Core: external login, the identity provider and every
// server-rendered page would break, and only in an installed, production build. So each
// apps/**/ClientApp/ngsw-config.json must list Spark's exclusions in navigationUrls, and each
// app's production build must actually use that file.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
import { strict as assert } from 'node:assert';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SKIP = new Set(['node_modules', 'bin', 'obj', 'dist', '.angular', '.nx', 'out-tsc']);

/** Angular's defaults restated (navigationUrls replaces them), then Spark's server-only paths. */
export const SPARK_NAVIGATION_URLS = [
  '/**',
  '!/**/*.*',
  '!/**/*__*',
  '!/**/*__*/**',
  '!/spark/**',
  '!/signin-*',
  '!/signout-*',
  '!/connect/**',
  '!/.well-known/**',
];

/**
 * App-specific server-only paths. `/**`-style exclusions do not cover the bare path, so each
 * prefix is listed both ways (S4).
 */
export const APP_NAVIGATION_URLS = {
  CodeCoverage: ['!/api', '!/api/**', '!/badge', '!/badge/**', '!/health', '!/health/**'],
};

/** Every directory under apps/ named ClientApp that holds an Angular project.json. */
export function findClientApps(root = path.join(REPO, 'apps')) {
  const found = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (!entry.isDirectory() || SKIP.has(entry.name)) continue;
      const full = path.join(dir, entry.name);
      if (entry.name === 'ClientApp' && existsSync(path.join(full, 'project.json'))) found.push(full);
      else walk(full);
    }
  };
  walk(root);
  return found.sort();
}

const readJson = (file) => JSON.parse(readFileSync(file, 'utf8'));
const appName = (clientApp) => path.relative(path.join(REPO, 'apps'), clientApp).split(path.sep)[0];

const clientApps = findClientApps();

test('finds the six Spark apps', () => {
  const names = clientApps.map(appName);
  for (const name of ['CodeCoverage', 'DemoApp', 'Fleet', 'HR', 'QnA', 'SparkId']) {
    assert.ok(names.includes(name), `no ClientApp found for ${name} (found: ${names.join(', ')})`);
  }
});

for (const clientApp of clientApps) {
  const name = appName(clientApp);
  const rel = path.relative(REPO, clientApp).split(path.sep).join('/');
  const configFile = path.join(clientApp, 'ngsw-config.json');

  test(`${name}: has an ngsw-config.json`, () => {
    assert.ok(existsSync(configFile), `${rel}/ngsw-config.json is missing (see docs/guide-pwa.md)`);
  });
  if (!existsSync(configFile)) continue;
  const config = readJson(configFile);

  test(`${name}: navigationUrls keep Spark's server paths on the network`, () => {
    const urls = config.navigationUrls;
    assert.ok(Array.isArray(urls), 'navigationUrls must be set: Angular\'s default sends /spark/** to the cached index.html');
    for (const url of [...SPARK_NAVIGATION_URLS, ...(APP_NAVIGATION_URLS[name] ?? [])]) {
      assert.ok(urls.includes(url), `navigationUrls lacks ${JSON.stringify(url)}`);
    }
  });

  test(`${name}: no dataGroup caches /spark`, () => {
    for (const group of config.dataGroups ?? []) {
      for (const url of group.urls ?? []) {
        assert.ok(!/^\/?spark\b/.test(url.replace(/^!/, '')), `dataGroup ${group.name} caches ${url}`);
      }
    }
  });

  test(`${name}: appData.build is set and is not a commit sha`, () => {
    // A sha changes on every commit: it busts the Nx cache and makes every client see an update (S4).
    // The value itself is not pinned (owner, 2026-10-08), so a package bump doesn't touch every app.
    const build = config.appData?.build;
    assert.ok(typeof build === 'string' && build.trim() !== '', 'appData.build must be a non-empty string');
    assert.ok(!/^[0-9a-f]{7,40}$/i.test(build), `appData.build looks like a commit sha (${build})`);
  });

  test(`${name}: the production build uses this ngsw-config.json`, () => {
    const project = readJson(path.join(clientApp, 'project.json'));
    const serviceWorker = project.targets?.build?.configurations?.production?.serviceWorker;
    assert.equal(serviceWorker, `${rel}/ngsw-config.json`);
  });

  test(`${name}: index.html links the manifest and app.config.ts registers the worker`, () => {
    const html = readFileSync(path.join(clientApp, 'src', 'index.html'), 'utf8');
    assert.match(html, /<link\s+rel="manifest"\s+href="manifest\.webmanifest">/);
    assert.ok(existsSync(path.join(clientApp, 'public', 'manifest.webmanifest')), 'public/manifest.webmanifest is missing');
    const appConfig = readFileSync(path.join(clientApp, 'src', 'app', 'app.config.ts'), 'utf8');
    assert.match(appConfig, /provideSparkServiceWorker\(/);
  });
}
