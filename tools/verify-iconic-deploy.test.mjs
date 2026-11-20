// iconic.mintplayer.com's deployment files (docs/iconic_plan.md "Deploy"): apps/Iconic/docker-compose.yml,
// .github/workflows/iconic-deploy.yml and iconic-image-check.yml. Nothing here runs Docker; it checks the files against
// each other and against the code (as verify-mintplayer-deploy.test.mjs):
//
// - the app, pull-only, pinned, joining Traefik's `web` (with traefik.docker.network=web) and the two backend networks
//   it shares with CodeCoverage (iconic-raven, iconic-mail; their members, TLS and the deploys' part:
//   verify-backend-networks.test.mjs);
// - the router serves iconic.mintplayer.com, hard-coded, the host the app's passkeys and mailed links name;
// - production sends mail through the relay and never through the pickup folder (the two refuse to start together);
// - the workflows' path filters, the Dockerfile's COPY lines and the csproj graph name the same projects, and the
//   filters include the TypeScript libraries the client build compiles from source;
// - the deploy uses only CodeCoverage's secret names, and its readiness bound covers the healthcheck's start period;
// - every ${VAR} of the compose file is documented in apps/Iconic/.env.example.
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

/** The transitive ProjectReference closure of a csproj, as repo-relative posix paths. Not imported from another
 *  verify-*.test.mjs: importing a test file registers its tests in this run too. */
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

const COMPOSE = 'apps/Iconic/docker-compose.yml';
const DEPLOY = '.github/workflows/iconic-deploy.yml';
const CHECK = '.github/workflows/iconic-image-check.yml';
const APP_CSPROJ = 'apps/Iconic/Iconic/Iconic.csproj';
const APP_DOCKERFILE = 'apps/Iconic/Iconic/Dockerfile';
const APP = 'iconic-app';
const HOST = 'iconic.mintplayer.com';

const compose = yaml(COMPOSE);
const services = compose.services ?? {};
const networksOf = (service) => (Array.isArray(service.networks) ? service.networks : Object.keys(service.networks ?? {}));
const pathsOf = (workflow, event) => yaml(workflow).on[event].paths;
const libFilters = (paths) => paths.filter((p) => p.startsWith('libs/') && p.endsWith('/**') && !p.startsWith('libs/node_packages/')).map((p) => p.slice(0, -3)).sort();

test('one service, the app, pull-only, pinned, with a limit and a healthcheck', () => {
  assert.deepEqual(Object.keys(services), [APP]);
  const app = services[APP];
  assert.equal(app.image, 'ghcr.io/mintplayer/iconic:master');
  assert.equal(app.build, undefined, 'pull-only (no build block)');
  assert.equal(app.ports, undefined, 'publishes no port');
  assert.ok(app.mem_limit, 'has a mem_limit');
  assert.ok(app.healthcheck?.test, 'has a healthcheck');
});

test('the app joins Traefik and the backend networks it shares with CodeCoverage', () => {
  assert.deepEqual(networksOf(services[APP]).sort(), ['iconic-mail', 'iconic-raven', 'web']);
  assert.equal(compose.networks.web?.external, true);
  for (const network of ['iconic-raven', 'iconic-mail']) {
    assert.deepEqual(compose.networks[network], { external: true, name: network }, `${network} is external, by name`);
  }
});

test('the router serves the app\'s own host, hard-coded, the one its passkeys and mailed links name', () => {
  const labels = services[APP].labels;
  assert.ok(labels.includes('traefik.docker.network=web'), 'traefik.docker.network=web (several networks)');
  assert.ok(labels.includes(`traefik.http.routers.iconic.rule=Host(\`${HOST}\`)`));
  assert.ok(labels.includes('traefik.http.services.iconic.loadbalancer.server.port=8080'));
  assert.ok(!labels.some((l) => l.startsWith('traefik.') && l.includes('${')), 'no Traefik label is interpolated');
  const settings = json('apps/Iconic/Iconic/appsettings.json');
  assert.equal(settings.Iconic.PasskeyServerDomain, HOST, 'the passkey RP id is the routed host');
  assert.equal(new URL(settings.Iconic.PublicBaseUrl).host, HOST, 'shared links are built from the routed host');
  assert.equal(new URL(json('apps/Iconic/Iconic/appsettings.Production.json').Spark.Auth.PublicBaseUrl).host, HOST, 'mailed links too');
});

test('the healthcheck probes the health endpoint the app maps', () => {
  assert.match(JSON.stringify(services[APP].healthcheck.test), /GET \/health /);
  assert.match(read('apps/Iconic/Iconic/Program.cs'), /MapHealthChecks\("\/health"\)\.AllowAnonymous\(\)/);
});

test('production mails through the relay, never through the pickup folder', () => {
  const env = services[APP].environment;
  assert.ok(env.some((e) => e.startsWith('Spark__Mail__Smtp__Host=')), 'Spark:Mail:Smtp:Host is set');
  assert.ok(!env.some((e) => e.startsWith('Spark__Mail__PickupFolder')), 'no PickupFolder in the deployment');
  for (const file of ['appsettings.json', 'appsettings.Production.json']) {
    assert.equal(json(`apps/Iconic/Iconic/${file}`).Spark?.Mail?.PickupFolder, undefined, `${file} sets no PickupFolder (both set refuses to start)`);
  }
});

test('the Dockerfile and both workflows name exactly the csproj graph, and the TypeScript libraries the client compiles', () => {
  const closure = projectClosure(APP_CSPROJ);
  const copied = [...read(APP_DOCKERFILE).matchAll(/^COPY \["([^"]+\.csproj)"/gm)].map((m) => m[1]).sort();
  assert.deepEqual(copied, closure, 'the Dockerfile COPYs every csproj of the closure before restore, and nothing else');

  const libs = closure.filter((p) => p.startsWith('libs/')).map((p) => path.posix.dirname(p)).sort();
  assert.deepEqual(libFilters(pathsOf(DEPLOY, 'push')), libs, 'iconic-deploy.yml deploys on a change to any library of the closure');
  const apps = closure.filter((p) => p.startsWith('apps/')).map((p) => `${path.posix.dirname(p)}/**`);
  for (const filter of apps) assert.ok(pathsOf(DEPLOY, 'push').includes(filter), `the deploy filter has ${filter}`);
  // The solver is compiled from source through tsconfig.base.json's paths: a change to it changes the image.
  const tsPaths = json('tsconfig.base.json').compilerOptions.paths;
  const solver = tsPaths['@mintplayer/constraint-solver'][0].replace(/^\.\//, '').split('/').slice(0, 3).join('/');
  assert.ok(pathsOf(DEPLOY, 'push').includes(`${solver}/**`), `the deploy filter has ${solver}/**`);
  const deployPaths = pathsOf(DEPLOY, 'push').filter((p) => !p.startsWith('.github/'));
  const checkPaths = pathsOf(CHECK, 'pull_request').filter((p) => !p.startsWith('.github/'));
  assert.deepEqual(checkPaths, deployPaths, 'iconic-image-check.yml mirrors the deploy\'s filter');
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
  const example = read('apps/Iconic/.env.example');
  for (const name of used) assert.match(example, new RegExp(`^#?${name}=`, 'm'), `${name} is in apps/Iconic/.env.example`);
});
