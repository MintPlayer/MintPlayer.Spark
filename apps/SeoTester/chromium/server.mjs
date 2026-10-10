// SeoTester's Chromium server (docs/seo_analyzer_PRD.md §6, D3, D6; docs/seotester/README.md "The Chromium container").
// Launches one headless Chromium with the hardening flags fixed HERE, on the server side, and serves Playwright's
// protocol on ws://0.0.0.0:3000/. SeoTester connects with ConnectAsync (SeoTester:Browser:Mode = Connect) and routes
// every request of every page itself; this browser never resolves a name or opens a connection to a site.
//
// Why not `playwright run-server`: it drops the client's launch arguments unless started with --unsafe (spike S3,
// measured: prefetch, sendBeacon, a keepalive fetch and <a ping> then reached a loopback listener). Here the client
// cannot change them at all. Keep ARGS equal to ChromiumBrowser.HardeningArgs (a test compares them).
//
// PLAYWRIGHT_CORE: where playwright-core is (the image installs it; the tests point it at Microsoft.Playwright's driver).
const core = await import(process.env.PLAYWRIGHT_CORE ?? 'playwright-core');
const chromium = core.chromium ?? core.default.chromium;

export const ARGS = [
  '--host-resolver-rules=MAP * ~NOTFOUND',
  '--webrtc-ip-handling-policy=disable_non_proxied_udp',
  '--force-webrtc-ip-handling-policy',
  '--disable-background-networking',
  '--disable-component-update',
  '--disable-domain-reliability',
  '--disable-sync',
  '--no-pings',
  '--dns-prefetch-disable',
];

const server = await chromium.launchServer({
  host: process.env.HOST ?? '0.0.0.0',
  port: Number(process.env.PORT ?? 3000),
  wsPath: '/',
  headless: true,
  args: ARGS,
  // Every connection the router does not fulfil goes to a proxy that does not exist: refused, loopback included.
  proxy: { server: 'http://127.0.0.1:9', bypass: '<-loopback>' },
});
console.log(`Chromium ${server.process().pid} listening on ${server.wsEndpoint()}`);

for (const signal of ['SIGINT', 'SIGTERM'])
  process.on(signal, async () => { await server.close(); process.exit(0); });
