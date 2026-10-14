import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * Guard for docs/form_control_directive_imports_PRD.md. ng-bootstrap puts `.form-control` on an
 * input through `BsFormControlDirective` (selector `bs-form input:not(.no-form-control)`), and
 * Angular matches a directive only against the imports of the component whose template holds the
 * element. A parent's `<bs-form>` does not cover a child component's inputs, and nothing fails when
 * the import is missing: the input simply renders unstyled. Because the selector matches a plain
 * `<input>`, the import always looks unused, so a cleanup removed it from the reference picker
 * (#512) and it was never added to the connected-agents page (#509).
 *
 * So every component template with a text-like `<input>` or a `<textarea>` must import the
 * directive, unless the element opts out with `no-form-control`, carries `form-control` itself, or
 * the file is allow-listed below with its reason.
 */
const libRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(libRoot, '../../..');

/** Repo-relative path → why its inputs deliberately go without the directive. */
const allowList: Record<string, string> = {
  'libs/node_packages/ng-spark/column-filter/src/spark-column-filter-panel.component.ts':
    'styles its search input itself (.filter-search); it renders outside any bs-form',
};

function walk(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === 'dist' || name === 'out-tsc' || name === 'obj' || name === 'bin' || name.startsWith('.')) continue;
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (name.endsWith('.ts') && !name.endsWith('.spec.ts')) out.push(full);
  }
  return out;
}

const appsDir = path.join(repoRoot, 'apps');
const appSources = readdirSync(appsDir)
  .map(name => path.join(appsDir, name, name, 'ClientApp', 'src'))
  .filter(dir => existsSync(dir));

const componentFiles = [libRoot, ...appSources]
  .flatMap(dir => walk(dir))
  .filter(file => readFileSync(file, 'utf8').includes('@Component('));

/** The component's template: the inline `template:` literal, or the file `templateUrl` names. */
function templateOf(file: string, source: string): string {
  const url = /templateUrl:\s*['"]([^'"]+)['"]/.exec(source);
  if (url) return readFileSync(path.resolve(path.dirname(file), url[1]), 'utf8');
  const inline = /template:\s*`([\s\S]*?)`/.exec(source);
  return inline ? inline[1] : '';
}

/** Inputs the directive should style: text-like inputs and textareas without an opt-out. */
function unstyledFormInputs(template: string): string[] {
  return [...template.matchAll(/<(input|textarea)\b[^>]*>/g)]
    .map(match => match[0])
    .filter(tag => !/\stype="(checkbox|radio|hidden|file|range|color)"/.test(tag))
    .filter(tag => !/\bclass="[^"]*\b(no-form-control|form-control)\b[^"]*"/.test(tag))
    .map(tag => tag.replace(/\s+/g, ' '));
}

const importsDirective = (source: string) => /imports:\s*\[[^\]]*\bBsFormControlDirective\b/.test(source);

describe('templates with form inputs import BsFormControlDirective', () => {
  it('finds the component templates', () => {
    expect(componentFiles.length).toBeGreaterThan(50);
  });

  it('every component whose template renders a text input or textarea imports the directive', () => {
    const offenders = componentFiles.flatMap(file => {
      const relative = path.relative(repoRoot, file).replace(/\\/g, '/');
      if (relative in allowList) return [];
      const source = readFileSync(file, 'utf8');
      const inputs = unstyledFormInputs(templateOf(file, source));
      return inputs.length && !importsDirective(source) ? [`${relative}: ${inputs.join(' | ')}`] : [];
    });

    expect(offenders).toEqual([]);
  });

  it('keeps the allow-list honest: every entry still exists', () => {
    for (const relative of Object.keys(allowList)) {
      expect(existsSync(path.join(repoRoot, relative)), relative).toBe(true);
    }
  });
});
