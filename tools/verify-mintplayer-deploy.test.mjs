// mintplayer.com's deployment files (docs/mintplayer_website_PRD.md A5–A7, G3; plan step 0.6):
// apps/MintPlayer/docker-compose.yml, .github/workflows/mintplayer-deploy.yml and mintplayer-image-check.yml. Nothing
// here runs Docker; it checks the files against each other and against the code (as verify-seotester-deploy.test.mjs):
//
// - one service, pull-only, pinned, joining Traefik's `web` (with traefik.docker.network=web) and the two backend networks
//   it shares with CodeCoverage (mintplayer-raven, mintplayer-mail; their members, TLS and the deploys' part:
//   verify-backend-networks.test.mjs);
// - the router serves the apex and www, hard-coded, and redirects www to the apex (PRD A7: the passkey RP id);
// - production sends mail through the relay and never through the pickup folder (the two refuse to start together);
// - the workflows' path filters, the Dockerfile's COPY lines and the csproj graph name the same projects;
// - the deploy uses only CodeCoverage's secret names, and its readiness bound covers the healthcheck's start period;
// - every ${VAR} of the compose file is documented in apps/MintPlayer/.env.example.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs); CI runs it in pull-request.yml.
import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (rel) => readFileSync(path.join(REPO, rel), 'utf8');
const posix = (p) => p.split(path.sep).join('/');

/** The transitive ProjectReference closure of a csproj, as repo-relative posix paths. Not imported from
 *  verify-seotester-deploy.test.mjs: importing a test file registers its tests in this run too. */
function projectClosure(csproj) {
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
const yaml = (rel) => YAML.parse(read(rel));
const json = (rel) => JSON.parse(read(rel).replace(/^\s*\/\/.*$/gm, ''));

const COMPOSE = 'apps/MintPlayer/docker-compose.yml';
const DEPLOY = '.github/workflows/mintplayer-deploy.yml';
const CHECK = '.github/workflows/mintplayer-image-check.yml';
const APP_CSPROJ = 'apps/MintPlayer/MintPlayer/MintPlayer.csproj';
const APP_DOCKERFILE = 'apps/MintPlayer/MintPlayer/Dockerfile';
const APP = 'mintplayer-app';

const compose = yaml(COMPOSE);
const services = compose.services ?? {};
const networksOf = (service) => (Array.isArray(service.networks) ? service.networks : Object.keys(service.networks ?? {}));
const pathsOf = (workflow, event) => yaml(workflow).on[event].paths;
const libFilters = (paths) => paths.filter((p) => p.startsWith('libs/') && p.endsWith('/**') && !p.startsWith('libs/node_packages/')).map((p) => p.slice(0, -3)).sort();

test('one service, pull-only, pinned, with limits and a healthcheck', () => {
  assert.deepEqual(Object.keys(services), [APP]);
  const app = services[APP];
  assert.equal(app.image, 'ghcr.io/mintplayer/mintplayer:master');
  assert.equal(app.build, undefined, 'pull-only (no build block)');
  assert.equal(app.ports, undefined, 'no published port (Traefik reaches the app on web)');
  assert.ok(app.mem_limit, 'mem_limit');
  assert.ok(app.healthcheck?.test, 'a healthcheck');
});

test('the app joins Traefik and the backend networks it shares with CodeCoverage', () => {
  assert.deepEqual(networksOf(services[APP]).sort(), ['mintplayer-mail', 'mintplayer-raven', 'web']);
  assert.equal(compose.networks.web?.external, true);
  for (const network of ['mintplayer-raven', 'mintplayer-mail']) {
    assert.deepEqual(compose.networks[network], { external: true, name: network }, `${network} is external, by name`);
  }
});

test('the router serves the apex and www, hard-coded, and sends www to the apex', () => {
  const labels = services[APP].labels;
  const host = new URL(json('apps/MintPlayer/MintPlayer/appsettings.Production.json').Spark.Auth.PublicBaseUrl).host;
  assert.equal(host, 'mintplayer.com', 'PublicBaseUrl is the apex (PRD A7)');
  assert.ok(labels.includes('traefik.docker.network=web'), 'traefik.docker.network=web (several networks)');
  assert.ok(labels.includes('traefik.http.routers.mintplayer.rule=Host(`mintplayer.com`) || Host(`www.mintplayer.com`)'));
  assert.ok(labels.includes('traefik.http.routers.mintplayer.middlewares=mintplayer-www-to-apex'));
  assert.ok(labels.includes('traefik.http.middlewares.mintplayer-www-to-apex.redirectregex.permanent=true'));
  assert.ok(labels.includes('traefik.http.services.mintplayer.loadbalancer.server.port=8080'));
  assert.ok(!labels.some((l) => l.startsWith('traefik.') && l.includes('${')), 'no Traefik label is interpolated');

  // The redirect, as Traefik applies it (Go regexp; compose turns $$ into $).
  const label = (key) => labels.find((l) => l.startsWith(`traefik.http.middlewares.mintplayer-www-to-apex.redirectregex.${key}=`)).split('=').slice(1).join('=');
  const regex = new RegExp(label('regex'));
  const replacement = label('replacement').replaceAll('$$', '$');
  assert.equal('https://www.mintplayer.com/po/song/Songs%2F1?x=1'.replace(regex, replacement), 'https://mintplayer.com/po/song/Songs%2F1?x=1');
  assert.ok(!regex.test('https://mintplayer.com/'), 'the apex is not redirected');
});

test('the healthcheck probes the health endpoint the app maps', () => {
  assert.match(JSON.stringify(services[APP].healthcheck.test), /GET \/health /);
  assert.match(read('apps/MintPlayer/MintPlayer/Program.cs'), /MapHealthChecks\("\/health"\)\.AllowAnonymous\(\)/);
});

test('production mails through the relay, never through the pickup folder', () => {
  const env = services[APP].environment;
  assert.ok(env.some((e) => e.startsWith('Spark__Mail__Smtp__Host=')), 'Spark:Mail:Smtp:Host is set');
  assert.ok(!env.some((e) => e.startsWith('Spark__Mail__PickupFolder')), 'no PickupFolder in the deployment');
  for (const file of ['appsettings.json', 'appsettings.Production.json']) {
    assert.equal(json(`apps/MintPlayer/MintPlayer/${file}`).Spark?.Mail?.PickupFolder, undefined, `${file} sets no PickupFolder (both set refuses to start)`);
  }
});

test('the Dockerfile and both workflows name exactly the csproj graph', () => {
  const closure = projectClosure(APP_CSPROJ);
  const copied = [...read(APP_DOCKERFILE).matchAll(/^COPY \["([^"]+\.csproj)"/gm)].map((m) => m[1]).sort();
  assert.deepEqual(copied, closure, 'the Dockerfile COPYs every csproj of the closure before restore, and nothing else');

  const libs = closure.filter((p) => p.startsWith('libs/')).map((p) => path.posix.dirname(p)).sort();
  assert.deepEqual(libFilters(pathsOf(DEPLOY, 'push')), libs, 'mintplayer-deploy.yml deploys on a change to any library of the closure');
  const apps = closure.filter((p) => p.startsWith('apps/')).map((p) => `${path.posix.dirname(p)}/**`);
  for (const filter of apps) assert.ok(pathsOf(DEPLOY, 'push').includes(filter), `the deploy filter has ${filter}`);
  const deployPaths = pathsOf(DEPLOY, 'push').filter((p) => !p.startsWith('.github/'));
  const checkPaths = pathsOf(CHECK, 'pull_request').filter((p) => !p.startsWith('.github/'));
  assert.deepEqual(checkPaths, deployPaths, 'mintplayer-image-check.yml mirrors the deploy\'s filter');
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
  const example = read('apps/MintPlayer/.env.example');
  for (const name of used) assert.match(example, new RegExp(`^#?${name}=`, 'm'), `${name} is in apps/MintPlayer/.env.example`);
});
