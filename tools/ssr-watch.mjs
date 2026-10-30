// Development server bundle for an SSR app (docs/spark_ssr_PRD.md I12, M4): `nx run <project>:build:development-ssr`
// in watch mode, started by the .NET host through SparkBuilderPrerenderingExtensions.WatchBuild("build:ssr:watch").
//
// CI=1 turns off Angular's persistent build cache for this process only. The dev server (`npm start`) builds the same
// project at the same time, and two builds writing one .angular/cache can wedge its file watcher (CLAUDE.md).
// Usage, from the app's package.json: node ../../../../tools/ssr-watch.mjs <nx project>
import { spawn } from 'node:child_process';

// The builder appends its own `--watch` argument; this script is always in watch mode, so the rest is ignored.
const project = process.argv[2];
if (!project || !/^[@\w./-]+$/.test(project)) {
  console.error('usage: node tools/ssr-watch.mjs <nx project>');
  process.exit(2);
}

// One command string: npx is a .cmd on Windows and needs a shell, and Node deprecates an argument array with
// `shell: true` (DEP0190). The project name is checked above, so nothing is spliced that the shell could read.
const child = spawn(`npx nx run ${project}:build:development-ssr --watch --skip-nx-cache --output-style=stream`, {
  stdio: 'inherit',
  shell: true,
  env: { ...process.env, CI: '1' },
});
child.on('exit', code => process.exit(code ?? 1));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
