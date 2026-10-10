// SeoTester's deployment files (docs/seo_analyzer_PRD.md §1 "Hosting constraints", D3, D6, M12;
// docs/seotester/deploy.md): apps/SeoTester/docker-compose.yml, .github/workflows/seotester-deploy.yml and
// seotester-image-check.yml. Nothing here runs Docker; it checks the files against each other and against the code:
//
// - the Chromium container is reachable only by SeoTester and reaches nothing: its only network is `seo-browser`,
//   which is `internal: true` and holds exactly the two services; no service publishes a port;
// - images are pinned, and the Chromium tag is the Playwright version of its image, its playwright-core and the
//   app's Microsoft.Playwright (ConnectAsync needs the same major.minor on both sides);
// - the app joins Traefik's `web` (with traefik.docker.network=web) and CodeCoverage's internal network under the
//   name Docker gives it (project directory + key), and its Host rule is the public base URL's host;
// - the workflows' path filters, the Dockerfile's COPY lines and the csproj graph name the same projects, so a new
//   dependency cannot silently skip a deploy or fail the image's restore;
// - the deploy uses only CodeCoverage's secret names, and its readiness bound covers the healthcheck's start period;
// - every ${VAR} of the compose file is documented in apps/SeoTester/.env.example.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
// `yaml` is nx's own dependency (always installed with the workspace).
import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (rel) => readFileSync(path.join(REPO, rel), 'utf8');
const yaml = (rel) => YAML.parse(read(rel));
const posix = (p) => p.split(path.sep).join('/');

const COMPOSE = 'apps/SeoTester/docker-compose.yml';
const DEPLOY = '.github/workflows/seotester-deploy.yml';
const CHECK = '.github/workflows/seotester-image-check.yml';
const APP_CSPROJ = 'apps/SeoTester/SeoTester/SeoTester.csproj';
const APP_DOCKERFILE = 'apps/SeoTester/SeoTester/Dockerfile';
const CHROMIUM_DOCKERFILE = 'apps/SeoTester/chromium/Dockerfile';

const compose = yaml(COMPOSE);
const services = compose.services ?? {};
const APP = 'seotester-app';
const CHROMIUM = 'seo-chromium';

/** A service's networks as names (compose allows a list or a map). */
const networksOf = (service) => (Array.isArray(service.networks) ? service.networks : Object.keys(service.networks ?? {}));

/** The transitive ProjectReference closure of a csproj, as repo-relative posix paths. */
export function projectClosure(csproj) {
  const seen = new Set();
  const visit = (rel) => {
    if (seen.has(rel)) return;
    seen.add(rel);
    const dir = path.dirname(path.join(REPO, rel));
    for (const match of read(rel).matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
      visit(posix(path.relative(REPO, path.resolve(dir, match[1].replaceAll('\\', '/')))));
    }
  };
  visit(csproj);
  return [...seen].sort();
}

/** The `on.<event>.paths` filter of a workflow. */
const pathsOf = (workflow, event) => yaml(workflow).on[event].paths;

/** Path filters for libraries (`libs/<x>/<Project>/**`), as project directories. */
const libFilters = (paths) => paths.filter((p) => p.startsWith('libs/') && p.endsWith('/**') && !p.startsWith('libs/node_packages/')).map((p) => p.slice(0, -3)).sort();

test('only the app and the Chromium container are in the compose file', () => {
  assert.deepEqual(Object.keys(services).sort(), [CHROMIUM, APP].sort());
});

test('the Chromium container is reachable only by SeoTester and reaches nothing', () => {
  const browserNetwork = compose.networks?.['seo-browser'];
  assert.ok(browserNetwork, 'networks.seo-browser is declared');
  assert.equal(browserNetwork.internal, true, 'seo-browser is internal: true (no egress, PRD D6)');
  assert.deepEqual(networksOf(services[CHROMIUM]), ['seo-browser'], 'Chromium joins seo-browser only');
  const members = Object.entries(services).filter(([, s]) => networksOf(s).includes('seo-browser')).map(([name]) => name).sort();
  assert.deepEqual(members, [CHROMIUM, APP].sort(), 'seo-browser holds exactly the app and Chromium');
  for (const [name, service] of Object.entries(services)) {
    assert.equal(service.ports, undefined, `${name} publishes no port (Traefik reaches the app on web)`);
  }
});

test('the Chromium container has its shared memory, a memory limit, init and a healthcheck', () => {
  const chromium = services[CHROMIUM];
  assert.ok(chromium.shm_size, 'shm_size is set (Docker\'s 64 MB crashes renderers)');
  assert.ok(chromium.mem_limit, 'mem_limit is set');
  assert.equal(chromium.init, true, 'init: true reaps Chromium\'s processes');
  assert.ok(chromium.healthcheck?.test, 'a healthcheck');
  assert.ok(services[APP].mem_limit, 'the app has a memory limit too');
  assert.ok(services[APP].healthcheck?.test, 'the app has a healthcheck');
});

test('every image is pinned, and the Chromium tag is the Playwright version on both sides', () => {
  for (const [name, service] of Object.entries(services)) {
    const tag = service.image?.split(':')[1];
    assert.ok(tag && tag !== 'latest', `${name}: ${service.image} has a tag that is not latest`);
    assert.equal(service.build, undefined, `${name} is pull-only (no build block)`);
  }
  const dockerfile = read(CHROMIUM_DOCKERFILE);
  const base = /^FROM mcr\.microsoft\.com\/playwright:v([0-9.]+)-/m.exec(dockerfile)?.[1];
  const core = /playwright-core@([0-9.]+)/.exec(dockerfile)?.[1];
  const dotnet = /<PackageReference Include="Microsoft\.Playwright" Version="([^"]+)"/.exec(read(APP_CSPROJ))?.[1];
  assert.ok(base, 'the Chromium Dockerfile names a Playwright image version');
  assert.equal(core, base, 'playwright-core equals the image');
  assert.equal(dotnet, base, 'the app\'s Microsoft.Playwright equals the image');
  assert.equal(services[CHROMIUM].image, `ghcr.io/mintplayer/seotester-chromium:${base}`, 'compose pins the Chromium image to that version');
});

test('the app joins Traefik and CodeCoverage\'s internal network under its real name', () => {
  const app = services[APP];
  assert.deepEqual(networksOf(app).sort(), ['coverage-internal', 'seo-browser', 'web']);
  assert.equal(compose.networks.web?.external, true);
  // Docker names a project's network <project>_<key>; the project is the directory the CodeCoverage deploy uses.
  const ccDeploy = read('.github/workflows/code-coverage-deploy.yml');
  const ccDir = /cd (\/var\/www\/[\w-]+)/.exec(ccDeploy)?.[1];
  assert.ok(ccDir, 'code-coverage-deploy.yml cds into its directory');
  assert.ok(yaml('apps/CodeCoverage/docker-compose.yml').networks['coverage-internal'], 'CodeCoverage declares coverage-internal');
  assert.deepEqual(compose.networks['coverage-internal'], { external: true, name: `${path.posix.basename(ccDir)}_coverage-internal` });
  assert.match(read(DEPLOY), new RegExp(`docker network inspect ${path.posix.basename(ccDir)}_coverage-internal`), 'the deploy refuses to start without it');

  const labels = app.labels;
  assert.ok(labels.includes('traefik.docker.network=web'), 'traefik.docker.network=web (three networks)');
  const host = new URL(JSON.parse(read('apps/SeoTester/SeoTester/appsettings.Production.json').replace(/^\s*\/\/.*$/gm, '')).Spark.Auth.PublicBaseUrl).host;
  assert.ok(labels.includes(`traefik.http.routers.seotester.rule=Host(\`${host}\`)`), `the Host rule is ${host}, hard-coded`);
  assert.ok(labels.includes('traefik.http.services.seotester.loadbalancer.server.port=8080'));
});

test('the app reaches the browser at the service the compose file runs', () => {
  const settings = JSON.parse(read('apps/SeoTester/SeoTester/appsettings.Production.json').replace(/^\s*\/\/.*$/gm, ''));
  const endpoint = new URL(settings.SeoTester.Browser.Endpoint);
  assert.equal(endpoint.hostname, CHROMIUM);
  const port = /PORT \?\? (\d+)/.exec(read('apps/SeoTester/chromium/server.mjs'))?.[1];
  assert.equal(endpoint.port, port, 'server.mjs listens on the port the app connects to');
});

test('the healthcheck probes an endpoint the app has', () => {
  assert.match(JSON.stringify(services[APP].healthcheck.test), /GET \/health /);
  assert.match(read('apps/SeoTester/SeoTester/Health/HealthEndpoint.cs'), /Path => "\/health"/);
});

test('the Dockerfile and both workflows name exactly the csproj graph', () => {
  const closure = projectClosure(APP_CSPROJ);
  const copied = [...read(APP_DOCKERFILE).matchAll(/^COPY \["([^"]+\.csproj)"/gm)].map((m) => m[1]).sort();
  assert.deepEqual(copied, closure, 'the Dockerfile COPYs every csproj of the closure before restore, and nothing else');

  const libs = closure.filter((p) => p.startsWith('libs/')).map((p) => path.posix.dirname(p)).sort();
  assert.deepEqual(libFilters(pathsOf(DEPLOY, 'push')), libs, 'seotester-deploy.yml deploys on a change to any library of the closure');
  const deployPaths = pathsOf(DEPLOY, 'push').filter((p) => !p.startsWith('.github/'));
  const checkPaths = pathsOf(CHECK, 'pull_request').filter((p) => !p.startsWith('.github/'));
  assert.deepEqual(checkPaths, deployPaths, 'seotester-image-check.yml mirrors the deploy\'s filter');
  assert.ok(deployPaths.includes('apps/SeoTester/**'));
});

test('the deploy uses CodeCoverage\'s secret names and waits as long as the healthcheck allows', () => {
  const secrets = (text) => new Set([...text.matchAll(/secrets\.([A-Z0-9_]+)/g)].map((m) => m[1]));
  const known = secrets(read('.github/workflows/code-coverage-deploy.yml'));
  for (const name of secrets(read(DEPLOY))) assert.ok(known.has(name), `secrets.${name} is one CodeCoverage's deploy already uses`);

  const script = read(DEPLOY);
  const probes = Number(/seq 1 (\d+)/.exec(script)?.[1]);
  const interval = Number(/^\s*sleep (\d+)$/m.exec(script)?.[1]);
  const startPeriod = Number(/^(\d+)s$/.exec(services[APP].healthcheck.start_period)?.[1]);
  assert.ok(probes * interval >= startPeriod, `readiness bound ${probes * interval}s covers start_period ${startPeriod}s`);
});

test('every variable of the compose file is documented in .env.example', () => {
  const used = new Set([...read(COMPOSE).matchAll(/\$\{([A-Z0-9_]+)/g)].map((m) => m[1]));
  const example = read('apps/SeoTester/.env.example');
  for (const name of used) assert.match(example, new RegExp(`^#?${name}=`, 'm'), `${name} is in apps/SeoTester/.env.example`);
});
