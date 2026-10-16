// Refuses an ncc build whose dependencies do not come from this checkout's own node_modules.
//
// ncc (webpack) derives every module id from the module's path relative to the build directory. Built in
// an agent worktree (.claude/worktrees/<name>/) that has no node_modules of its own, Node resolves the
// dependencies from the MAIN checkout several levels up, so every id changes and the committed bundle no
// longer matches CI's `npm ci && npm run build` (the `mode: verify` drift check). The bytes do not depend
// on the OS or the Node 24 patch version: a Windows build (node 24.15.0, CRLF sources) and CI's Linux build
// (node 24.21.0) are byte-identical once both resolve from <checkout>/node_modules.
//
// Usage (package.json): "prebuild": "node ../../../tools/ncc-build-guard.mjs @vercel/ncc @actions/core"
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

const cwd = process.cwd();
let root = cwd;
for (;;) {
  const manifest = path.join(root, 'package.json');
  if (existsSync(manifest) && JSON.parse(readFileSync(manifest, 'utf8')).workspaces) break;
  const parent = path.dirname(root);
  if (parent === root) {
    console.error(`ncc-build-guard: no workspace root above ${cwd}`);
    process.exit(1);
  }
  root = parent;
}

const expected = path.join(root, 'node_modules') + path.sep;
// Node's lookup: the first <ancestor>/node_modules/<name> going up from the build directory. Done by hand
// because a package's `exports` can hide its package.json from require.resolve.
function packageDir(name) {
  for (let dir = cwd; ; dir = path.dirname(dir)) {
    const candidate = path.join(dir, 'node_modules', name);
    if (existsSync(path.join(candidate, 'package.json'))) return candidate;
    if (path.dirname(dir) === dir) return null;
  }
}

const foreign = [];
for (const name of process.argv.slice(2)) {
  const resolved = packageDir(name);
  if (!resolved) foreign.push(`${name}: not installed`);
  else if (!resolved.startsWith(expected)) foreign.push(`${name}: ${resolved}`);
}
if (foreign.length) {
  console.error(
    `ncc-build-guard: dependencies resolve from outside ${expected}\n  ${foreign.join('\n  ')}\n` +
      `The bundle's module ids would not match CI's build. Run \`npm ci\` in ${root} first.`,
  );
  process.exit(1);
}
