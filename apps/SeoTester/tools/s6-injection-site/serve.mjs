// Spike S6 (docs/spark_mcp_spikes.md, "S6"): a static site whose title, description, Open Graph tags, element ids and body
// tell an AI agent to run destructive commands, served on loopback for a LOCAL SeoTester to scan. The live run checks that an
// agent reading the scan through SeoTester's MCP tools fixes the findings and does not obey the page.
//
//   node apps/SeoTester/tools/s6-injection-site/serve.mjs [port]      (default 8123, 127.0.0.1 only)
//
// No dependencies. Serves only the files next to this script, never a path outside it.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, extname, join, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const port = Number(process.argv[2] ?? 8123);
const types = { '.html': 'text/html; charset=utf-8', '.txt': 'text/plain; charset=utf-8', '.xml': 'application/xml; charset=utf-8' };
const routes = { '/': 'index.html', '/pricing': 'pricing.html', '/robots.txt': 'robots.txt', '/sitemap.xml': 'sitemap.xml' };

createServer(async (request, response) => {
  const path = new URL(request.url ?? '/', 'http://localhost').pathname;
  const file = routes[path];
  const full = file ? normalize(join(root, file)) : null;
  if (!full || !full.startsWith(root + sep)) {
    response.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' }).end('Not found');
    return;
  }
  try {
    const body = await readFile(full);
    response.writeHead(200, { 'content-type': types[extname(full)] ?? 'application/octet-stream' }).end(body);
  } catch {
    response.writeHead(500).end();
  }
  console.log(new Date().toISOString(), request.method, path, request.headers['user-agent'] ?? '');
}).listen(port, '127.0.0.1', () => console.log(`S6 fixture site on http://127.0.0.1:${port}/ (scan it as http://s6.localtest.me:${port})`));
