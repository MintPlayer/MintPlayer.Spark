// The backend topology shared by CodeCoverage (production, coverage.mintplayer.com), SeoTester, mintplayer.com and the
// shared mail relay on one VPS (docs/seo_analyzer_PRD.md §12 D55, D56; docs/code-coverage/raven-tls-migration.md;
// docs/mintplayer_mail_plan.md). Nothing here runs Docker; it reads the four compose files (apps/CodeCoverage,
// apps/SeoTester, apps/MintPlayer, apps/MailRelay) and their deploy workflows, and holds them to the owner's "minimal
// network exposure" decisions:
//
// - networks are pairwise: each backend service is reachable only by the app container that uses it, and RavenDB and the
//   mail relay never share a network; every backend network is internal, only the relay has a way out;
// - a network two stacks share (seo-raven, mintplayer-raven, and one mail network per app) is external, that name, in
//   every file that joins it, and every deploy creates its own `--internal` when missing, before compose runs, with the
//   same block, and refuses one that is not internal;
// - mintplayer.com (docs/mintplayer_website_PRD.md G3) shares RavenDB and the relay the same way, on its own pair
//   (mintplayer-raven, mintplayer-mail), never on SeoTester's;
// - Chromium is only on seo-browser;
// - RavenDB is TLS-only (https/tcp URLs, no unsecured-access flag) and every app that reaches it presents a client
//   certificate and trusts the private CA; a key or certificate is mounted only into the container that needs it, and the
//   CodeCoverage deploy checks that every one of them is on the server before it starts anything.
//
// Run: npm run test:tools (node --test over tools/*.test.mjs).
import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = (rel) => readFileSync(path.join(REPO, rel), 'utf8');

const CC = 'apps/CodeCoverage/docker-compose.yml';
const SEO = 'apps/SeoTester/docker-compose.yml';
const CC_DEPLOY = '.github/workflows/code-coverage-deploy.yml';
const SEO_DEPLOY = '.github/workflows/seotester-deploy.yml';
const MP = 'apps/MintPlayer/docker-compose.yml';
const MP_DEPLOY = '.github/workflows/mintplayer-deploy.yml';
const MR = 'apps/MailRelay/docker-compose.yml';
const MR_DEPLOY = '.github/workflows/mail-relay-deploy.yml';
const MP_CROSS = ['mintplayer-raven', 'mintplayer-mail'];
const DEPLOYS = { [CC]: CC_DEPLOY, [SEO]: SEO_DEPLOY, [MP]: MP_DEPLOY, [MR]: MR_DEPLOY };

const stacks = { [CC]: YAML.parse(read(CC)), [SEO]: YAML.parse(read(SEO)), [MP]: YAML.parse(read(MP)), [MR]: YAML.parse(read(MR)) };
// A new file is LF in a Windows working copy while older ones are CRLF; CI checks both out as LF.
const readLf = (rel) => read(rel).replaceAll('\r\n', '\n');
const networksOf = (service) => (Array.isArray(service.networks) ? service.networks : Object.keys(service.networks ?? {}));
const env = (service) => Object.fromEntries((service.environment ?? []).map((e) => [e.slice(0, e.indexOf('=')), e.slice(e.indexOf('=') + 1)]));

/** The real network name of a compose network key (an external network's `name`, else the key). */
const realName = (compose, key) => compose.networks?.[key]?.name ?? key;

/** Every network across both stacks, by real name: its members ("<service>") and its declarations. */
function topology() {
  const nets = new Map();
  for (const [file, compose] of Object.entries(stacks)) {
    for (const [key, decl] of Object.entries(compose.networks ?? {})) {
      const name = realName(compose, key);
      const entry = nets.get(name) ?? { members: new Set(), decls: [] };
      entry.decls.push({ file, ...decl });
      nets.set(name, entry);
    }
    for (const [service, def] of Object.entries(compose.services ?? {})) {
      for (const key of networksOf(def)) {
        const name = realName(compose, key);
        assert.ok(nets.has(name), `${file}: ${service} joins ${key}, which is declared`);
        nets.get(name).members.add(service);
      }
    }
  }
  return nets;
}
const nets = topology();
const members = (name) => [...(nets.get(name)?.members ?? [])].sort();

test('each backend network holds exactly the services that need each other', () => {
  const expected = {
    'coverage-db': ['coverage-app', 'coverage-raven', 'coverage-raven-license'],
    // The relay is its own stack (apps/MailRelay), on one network per app plus its own way out.
    'coverage-mail': ['coverage-app', 'mail-relay'],
    'mail-relay-egress': ['mail-relay'],
    // The commit-graph sidecar (#504, D4/D18/D24): its unauthenticated API is shared with the app alone, and its way
    // out with nothing.
    'coverage-git': ['coverage-app', 'coverage-git'],
    'coverage-git-egress': ['coverage-git'],
    // The merge queue's git writer (M3): its unauthenticated, pushing API is shared with the app alone, its way out with
    // nothing.
    'coverage-git-writer': ['coverage-app', 'coverage-git-writer'],
    'coverage-git-writer-egress': ['coverage-git-writer'],
    'seo-raven': ['coverage-raven', 'seotester-app'],
    'seo-mail': ['mail-relay', 'seotester-app'],
    'mintplayer-raven': ['coverage-raven', 'mintplayer-app'],
    'mintplayer-mail': ['mail-relay', 'mintplayer-app'],
    'seo-browser': ['seo-chromium', 'seotester-app'],
    'seo-fetch': ['seo-fetcher', 'seotester-app'],
    'seo-egress': ['seo-fetcher'],
  };
  for (const [name, want] of Object.entries(expected)) assert.deepEqual(members(name), want.sort(), `${name} members`);
  assert.deepEqual(members('web'), ['coverage-app', 'mintplayer-app', 'seotester-app'], 'only the three apps are on Traefik\'s web');
  assert.ok(!nets.has('coverage-internal') && !nets.has('mintplayer-backend'), 'the old shared networks are gone (D48 superseded)');
});

test('RavenDB and the mail relay share no network, and neither has a way out except the relay\'s own', () => {
  for (const [name, { members: m }] of nets) {
    assert.ok(!(m.has('coverage-raven') && m.has('mail-relay')), `${name} carries both coverage-raven and mail-relay`);
  }
  const egress = [...nets].filter(([, n]) => n.decls.every((d) => d.internal !== true && d.external !== true)).map(([name]) => name).sort();
  // Non-internal networks of a stack: the external ones aside (Traefik's, and the shared backend networks the deploys
  // create internal, checked below), only the relay's egress network, the git sidecars' (commit graph D18) and the
  // fetcher's (PRD D54).
  assert.deepEqual(egress, ['coverage-git-egress', 'coverage-git-writer-egress', 'mail-relay-egress', 'seo-egress'], 'the only compose-created networks with a route out are the relay\'s, the two git sidecars\' and the fetcher\'s');
  assert.deepEqual(members('mail-relay-egress'), ['mail-relay'], 'the relay\'s egress network carries the relay alone');
  assert.deepEqual(networksOf(stacks[MR].services['mail-relay']).sort(), ['coverage-mail', 'mail-relay-egress', 'mintplayer-mail', 'seo-mail'], 'the relay is on its mail networks and its way out, nothing else');
  assert.deepEqual(members('coverage-git-writer-egress'), ['coverage-git-writer'], 'the git writer\'s egress network carries the writer alone');
  assert.deepEqual(members('seo-egress'), ['seo-fetcher'], 'the fetcher\'s egress network carries the fetcher alone (no database, no relay)');
  assert.deepEqual(members('coverage-git-egress'), ['coverage-git'], 'the git sidecar\'s egress network carries the sidecar alone (D18)');
  for (const name of ['coverage-db', 'coverage-git', 'coverage-git-writer', 'seo-browser', 'seo-fetch']) {
    assert.ok(nets.get(name).decls.every((d) => d.internal === true), `${name} is internal: true`);
  }
  const ravenNets = [...nets].filter(([, n]) => n.members.has('coverage-raven')).map(([name]) => name).sort();
  assert.deepEqual(ravenNets, ['coverage-db', 'mintplayer-raven', 'seo-raven'], 'RavenDB is only on internal networks (no egress, PRD D55)');
});

test('RavenDB and the commit-graph sidecar share no network, and the sidecar is only where D24 puts it', () => {
  for (const [name, { members: m }] of nets) {
    assert.ok(!(m.has('coverage-raven') && m.has('coverage-git')), `${name} carries both coverage-raven and coverage-git`);
  }
  assert.deepEqual(networksOf(stacks[CC].services['coverage-git']).sort(), ['coverage-git', 'coverage-git-egress'],
    'coverage-git is on its two networks only (never coverage-db, seo-raven or web)');
  assert.deepEqual(members('coverage-db'), ['coverage-app', 'coverage-raven', 'coverage-raven-license'], 'coverage-db keeps exactly its three members');

  const sidecar = stacks[CC].services['coverage-git'];
  assert.ok(!(sidecar.ports ?? []).length, 'the sidecar publishes no port');
  assert.ok((sidecar.volumes ?? []).includes('codecoverage-git-data:/data/git'), 'the mirrors are on the named volume');
  for (const [service, def] of Object.entries(stacks[CC].services)) {
    if (service === 'coverage-git') continue;
    assert.ok(!(def.volumes ?? []).some((v) => v.startsWith('codecoverage-git-data:')), `${service} must not mount the mirrors`);
  }
  assert.ok(sidecar.mem_limit, 'the sidecar has a memory limit (D22)');
  assert.equal(env(stacks[CC].services['coverage-app']).CommitGraph__SidecarUrl, 'http://coverage-git:8080', 'the app reaches the sidecar by its service name');
});

test('the merge queue\'s git writer shares no network with RavenDB or the read-only mirror, and is capped (M3, S8)', () => {
  for (const [name, { members: m }] of nets) {
    assert.ok(!(m.has('coverage-raven') && m.has('coverage-git-writer')), `${name} carries both coverage-raven and coverage-git-writer`);
    assert.ok(!(m.has('coverage-git') && m.has('coverage-git-writer')), `${name} carries both git sidecars (D2: the mirror stays read-only)`);
  }
  const writer = stacks[CC].services['coverage-git-writer'];
  assert.deepEqual(networksOf(writer).sort(), ['coverage-git-writer', 'coverage-git-writer-egress'], 'coverage-git-writer is on its two networks only');
  assert.ok(!(writer.ports ?? []).length, 'the writer publishes no port');
  assert.deepEqual(writer.volumes, ['codecoverage-git-writer-jobs:/data/jobs'], 'the writer mounts its jobs volume and nothing else (no key, no certificate)');
  for (const [service, def] of Object.entries(stacks[CC].services)) {
    if (service === 'coverage-git-writer') continue;
    assert.ok(!(def.volumes ?? []).some((v) => v.startsWith('codecoverage-git-writer-jobs:')), `${service} must not mount the writer's jobs`);
  }
  assert.equal(writer.mem_limit, '768m', 'S8: 512 MB per job plus the host');
  assert.equal(writer.pids_limit, 128, 'S8: 64 processes per job plus the host');
  assert.equal(writer.read_only, true, 'read-only root file system');
  assert.equal(env(writer).GitWriter__MaxConcurrentJobs, '1', 'the limits above are sized for one job');
  assert.equal(env(stacks[CC].services['coverage-app']).MergeQueue__GitWriterUrl, 'http://coverage-git-writer:8080', 'the app reaches the writer by its service name');
  const deploy = read(CC_DEPLOY);
  assert.match(deploy, /GIT_WRITER_IMAGE_NAME: mintplayer\/codecoverage-git-writer/, 'the deploy builds and pushes the writer image');
  assert.match(deploy, /needs: \[build-and-push, build-and-push-git, build-and-push-git-writer\]/, 'the deploy waits for the writer image, which compose pulls');
});

test('the shared networks are external in every stack that joins them, and every deploy creates its own internal before compose runs', () => {
  const blocks = new Set();
  for (const [file, workflow] of Object.entries(DEPLOYS)) {
    // Every external network but Traefik's is a shared backend network.
    const shared = Object.entries(stacks[file].networks ?? {}).filter(([key, d]) => d.external === true && key !== 'web');
    for (const [key, decl] of shared) {
      assert.deepEqual(decl, { external: true, name: key }, `${file} declares ${key} external, that name`);
      assert.ok(nets.get(key).decls.length >= 2, `${key} is shared by two stacks`);
    }
    const script = readLf(workflow);
    const loop = /for net in ([^;]+); do\n([\s\S]*?)\n\s*done/.exec(script);
    assert.ok(loop, `${workflow} loops over its shared networks`);
    assert.deepEqual(loop[1].trim().split(/\s+/).sort(), shared.map(([key]) => key).sort(), `${workflow} creates exactly its stack's shared networks`);
    const body = loop[2];
    assert.match(body, /docker network inspect "\$net" > \/dev\/null 2>&1 \|\| docker network create --internal "\$net"/, `${workflow} creates them --internal, only when missing`);
    assert.match(body, /docker network inspect -f '\{\{\.Internal\}\}' "\$net"\)" = "true" \] \|\| \{ echo "::error::/, `${workflow} refuses an existing network that is not internal`);
    const down = script.indexOf('docker compose --env-file .env down');
    if (down >= 0) assert.ok(loop.index < down, `${workflow} creates them before down`);
    assert.ok(loop.index < script.indexOf('docker compose --env-file .env up -d'), `${workflow} creates them before up`);
    assert.ok(!script.includes('mintplayer-backend'), `${workflow} no longer creates mintplayer-backend`);
    assert.ok(!script.includes('coverage-smtp'), `${workflow} no longer refers to coverage-smtp (the relay is mail-relay, its own stack)`);
    blocks.add(body.replace(/^[ \t]+/gm, ''));
  }
  assert.equal(blocks.size, 1, 'the same block in every deploy');
  const seo = readLf(SEO_DEPLOY);
  assert.match(seo, /docker network inspect seo-raven --format '\{\{range \.Containers\}\}\{\{\.Name\}\} \{\{end\}\}' \| grep -q 'coverage-raven' \|\| \{ echo "::error::/);
  // The relay is not required: mail waits in the app's queue (MailManager retries) until the relay joins.
  assert.match(seo, /docker network inspect seo-mail --format '\{\{range \.Containers\}\}\{\{\.Name\}\} \{\{end\}\}' \| grep -qw 'mail-relay' \|\| echo "::warning::/);
});

test('the mail relay deploy refuses to start without a DKIM key for every sender domain', () => {
  const script = readLf(MR_DEPLOY);
  assert.match(script, /test -f \.env \|\| \{ echo "::error::/, 'the relay\'s .env is server-managed and required');
  const dkim = script.indexOf('test -s "mail-dkim/$d.private"');
  assert.ok(dkim >= 0, 'every allowed sender domain needs its flat DKIM key');
  assert.ok(dkim < script.indexOf('docker compose --env-file .env up -d'), 'checked before up');
  const relay = stacks[MR].services['mail-relay'];
  assert.equal(relay.container_name, 'mail-relay', 'the apps and raven-cert-renew.sh reach it by that name');
  const defaults = /\$\{MAIL_SENDER_DOMAIN:-([^}]+)\}/.exec(env(relay).ALLOWED_SENDER_DOMAINS);
  assert.ok(defaults && script.includes(`domains="${defaults[1]}"`), 'the deploy\'s default domain list is the compose file\'s');
  for (const [file, service] of [[CC, 'coverage-app'], [SEO, 'seotester-app'], [MP, 'mintplayer-app']]) {
    assert.equal(env(stacks[file].services[service]).Spark__Mail__Smtp__Host, '${MAIL_HOST:-mail-relay}', `${service} sends through mail-relay`);
  }
});

test('mintplayer.com\'s networks are its own, never SeoTester\'s', () => {
  for (const name of MP_CROSS) assert.ok(!stacks[SEO].networks?.[name], `SeoTester does not join ${name}`);
  const mp = readLf(MP_DEPLOY);
  // No deploy order: the MintPlayer deploy attaches CodeCoverage's running database itself, under the service name.
  assert.match(mp, /for pair in coverage-raven:mintplayer-raven; do/);
  assert.match(mp, /docker network inspect mintplayer-mail --format '\{\{range \.Containers\}\}\{\{\.Name\}\} \{\{end\}\}' \| grep -qw 'mail-relay' \|\| echo "::warning::/);
  assert.match(mp, /label=com\.docker\.compose\.project=code-coverage/);
  assert.match(mp, /docker network connect --alias "\$svc" "\$net" "\$id"/);
  assert.ok(mp.indexOf('docker network connect') < mp.indexOf('docker compose --env-file .env down'), 'attached before down');
  assert.match(read(CC_DEPLOY), /register_app_cert mintplayer MintPlayer \/var\/www\/mintplayer\/raven-certs\/mintplayer\.crt/, 'the CodeCoverage deploy registers the app\'s certificate for its database');
});

test('Chromium is only on seo-browser', () => {
  assert.deepEqual(networksOf(stacks[SEO].services['seo-chromium']), ['seo-browser']);
});

test('RavenDB accepts TLS only, and every app that reaches it presents a certificate and trusts the CA', () => {
  const raven = env(stacks[CC].services['coverage-raven']);
  assert.equal(raven.RAVEN_Security_UnsecuredAccessAllowed, undefined, 'no unsecured-access flag');
  assert.match(raven.RAVEN_ServerUrl, /^https:\/\//);
  assert.match(raven.RAVEN_PublicServerUrl, /^https:\/\/coverage-raven:/, 'the public URL is the certificate\'s name');
  assert.match(raven.RAVEN_PublicServerUrl_Tcp, /^tcp:\/\/coverage-raven:/);
  assert.ok(raven.RAVEN_Security_Certificate_Path, 'a server certificate');
  assert.match(raven.RAVEN_Security_WellKnownCertificates_Admin, /^\$\{RAVEN_OPERATOR_THUMBPRINT:\?/, 'the operator is the only well-known admin, required');

  const clients = [[CC, 'coverage-app', 'Coverage'], [SEO, 'seotester-app', 'SeoTester'], [MP, 'mintplayer-app', 'MintPlayer']];
  for (const [file, name] of clients) {
    const service = stacks[file].services[name];
    const e = env(service);
    assert.match(e.Spark__RavenDb__Urls__0, /^https:\/\/coverage-raven:/, `${name} uses https`);
    const mounted = new Set((service.volumes ?? []).map((v) => v.split(':')[1]));
    for (const key of ['Spark__RavenDb__CertificateFile', 'Spark__RavenDb__CertificateKeyFile', 'Spark__RavenDb__ServerCertificateAuthorityFile']) {
      assert.ok(e[key], `${name} sets ${key}`);
      assert.ok(mounted.has(e[key]), `${name}: ${e[key]} is a mounted file`);
    }
    assert.equal(e.Spark__RavenDb__EnsureDatabaseCreated, 'false', `${name} does not try to create its database (cluster admin)`);
  }
  // Every service that joins a network with coverage-raven is RavenDB itself, an app above or the licence one-shot.
  const ravenPeers = new Set([...nets].filter(([, n]) => n.members.has('coverage-raven')).flatMap(([, n]) => [...n.members]));
  assert.deepEqual([...ravenPeers].sort(), ['coverage-app', 'coverage-raven', 'coverage-raven-license', 'mintplayer-app', 'seotester-app']);
  const license = stacks[CC].services['coverage-raven-license'].command;
  assert.ok(license.includes('--cert') && license.includes('--key') && license.includes('--cacert'), 'the licence one-shot authenticates and checks the server');
  assert.match(license.at(-1), /^https:\/\/coverage-raven:8080\/admin\/license\/activate$/);
});

test('no key or certificate is mounted into a container that does not need it', () => {
  const allowed = {
    'server.pfx': ['coverage-raven'],
    'operator.crt': ['coverage-raven-license'],
    'operator.key': ['coverage-raven-license'],
    'codecoverage.crt': ['coverage-app'],
    'codecoverage.key': ['coverage-app'],
    'seotester.crt': ['seotester-app'],
    'seotester.key': ['seotester-app'],
    'mintplayer.crt': ['mintplayer-app'],
    'mintplayer.key': ['mintplayer-app'],
    'ca.crt': ['coverage-app', 'coverage-raven-license', 'mintplayer-app', 'seotester-app'],
  };
  const seen = {};
  for (const compose of Object.values(stacks)) {
    for (const [service, def] of Object.entries(compose.services ?? {})) {
      for (const volume of def.volumes ?? []) {
        const source = volume.split(':')[0];
        if (!/\.(pfx|p12|key|crt|pem)$/.test(source) || source.endsWith('github-app.pem')) continue;
        const file = path.posix.basename(source);
        assert.ok(allowed[file], `${service} mounts ${source}, which is not a known certificate file`);
        assert.ok(allowed[file].includes(service), `${service} must not mount ${source}`);
        assert.match(volume, /:ro$/, `${service} mounts ${source} read-only`);
        (seen[file] ??= new Set()).add(service);
      }
    }
  }
  for (const [file, services] of Object.entries(allowed)) assert.deepEqual([...(seen[file] ?? [])].sort(), [...services].sort(), `${file} is mounted exactly where needed`);
});

test('each deploy checks, before compose, that every certificate file its stack mounts is on the server', () => {
  for (const [file, workflow] of [[CC, CC_DEPLOY], [SEO, SEO_DEPLOY], [MP, MP_DEPLOY]]) {
    const script = read(workflow);
    const sources = Object.values(stacks[file].services).flatMap((s) => (s.volumes ?? []).map((v) => v.split(':')[0]))
      .filter((s) => /^\.\/raven-(certs|operator)\//.test(s)).map((s) => s.slice(2));
    const loop = /for f in ([^;]+); do\s+test -s "\$f"/.exec(script);
    assert.ok(loop, `${workflow} checks certificate files with test -s`);
    assert.deepEqual([...new Set(loop[1].trim().split(/\s+/))].sort(), [...new Set(sources)].sort(), `${workflow} checks exactly the mounted files`);
    assert.ok(loop.index < script.indexOf('docker compose --env-file .env down'), `${workflow} checks before down`);
  }
  const cc = read(CC_DEPLOY);
  const used = [...read(CC).matchAll(/\$\{(RAVEN_[A-Z0-9_]+)/g)].map((m) => m[1]);
  assert.deepEqual([...new Set(used)].sort(), ['RAVEN_OPERATOR_THUMBPRINT', 'RAVEN_SERVER_CERTIFICATE_PASSWORD']);
  for (const name of used) {
    assert.match(read('apps/CodeCoverage/.env.example'), new RegExp(`^${name}=`, 'm'), `${name} is in apps/CodeCoverage/.env.example`);
    assert.ok(cc.includes(name), `the CodeCoverage deploy checks ${name} is set in .env`);
  }
});
