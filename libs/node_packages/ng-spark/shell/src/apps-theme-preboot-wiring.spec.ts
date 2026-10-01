import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * #462 G2 guard. `spark-shell` themes the page from Angular, but the FIRST paint is themed by
 * `bs-theme-preboot.js`, a classic blocking script every app must copy into its output and load
 * from `index.html` before any stylesheet. A missing copy is a silent 404 and the flash of the
 * wrong theme comes back, with nothing failing. So this pins the wiring of every app in `apps/`.
 *
 * It reads the source `index.html` and `project.json` rather than a build output: building five
 * apps is not a unit test. Angular appends the stylesheet `<link>`s at the end of `<head>`, after
 * the script, so a script placed before any authored stylesheet stays first in the built file.
 */
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../..');
const appsDir = path.join(repoRoot, 'apps');
const prebootDir = 'node_modules/@mintplayer/web-components/theming';

const apps = readdirSync(appsDir)
  .map(name => ({ name, client: path.join(appsDir, name, name, 'ClientApp') }))
  .filter(app => existsSync(path.join(app.client, 'project.json')));

describe('apps load bs-theme-preboot.js before any stylesheet (#462 G2)', () => {
  it('finds the Spark apps', () => {
    expect(apps.map(a => a.name)).toEqual(expect.arrayContaining(['CodeCoverage', 'DemoApp', 'Fleet', 'HR']));
  });

  it('the shipped pre-boot script exists where the assets globs copy it from, and reads the default-mode meta', () => {
    const file = path.join(repoRoot, prebootDir, 'bs-theme-preboot.js');
    expect(existsSync(file)).toBe(true);
    expect(readFileSync(file, 'utf8')).toContain('bs-theme-default-mode');
  });

  describe.each(apps)('$name', ({ client }) => {
    const html = readFileSync(path.join(client, 'src', 'index.html'), 'utf8');

    it('index.html references the script after <base>, before any stylesheet, blocking', () => {
      const tag = /<script\b[^>]*\bsrc="bs-theme-preboot\.js"[^>]*>\s*<\/script>/.exec(html);
      expect(tag, 'no <script src="bs-theme-preboot.js">').not.toBeNull();
      expect(tag![0]).not.toMatch(/\b(async|defer|type="module")\b/);

      const at = tag!.index;
      // Its src is relative: before <base> it would resolve against a deep link's path.
      expect(html.indexOf('<base ')).toBeGreaterThan(-1);
      expect(html.indexOf('<base ')).toBeLessThan(at);
      const stylesheets = [...html.matchAll(/<link\b[^>]*\brel=["']?stylesheet/gi)].map(m => m.index!);
      const styles = [...html.matchAll(/<style\b/gi)].map(m => m.index!);
      for (const i of [...stylesheets, ...styles]) {
        expect(i).toBeGreaterThan(at);
      }
    });

    it('declares the default mode before the script (it is read synchronously) and the colour-scheme metas', () => {
      const script = html.indexOf('bs-theme-preboot.js"');
      const meta = html.search(/<meta\s+name="bs-theme-default-mode"\s+content="auto">/);
      expect(meta).toBeGreaterThan(-1);
      expect(meta).toBeLessThan(script);
      expect(html).toMatch(/<meta\s+name="color-scheme"\s+content="light dark">/);
      expect(html).toMatch(/<meta\s+name="theme-color"\s+media="\(prefers-color-scheme: light\)"/);
      expect(html).toMatch(/<meta\s+name="theme-color"\s+media="\(prefers-color-scheme: dark\)"/);
    });

    it('project.json copies the script to the output root', () => {
      const project = JSON.parse(readFileSync(path.join(client, 'project.json'), 'utf8'));
      const assets: unknown[] = project.targets?.build?.options?.assets ?? [];
      expect(assets).toContainEqual({ glob: 'bs-theme-preboot.js', input: prebootDir, output: '/' });
    });
  });
});
