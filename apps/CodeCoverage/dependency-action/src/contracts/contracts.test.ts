import * as fs from 'node:fs';
import * as path from 'node:path';
import { CONTRACTS } from '../testing/helpers';
import { KEYS } from './types';
import { isValid, schemas, type SchemaName } from './validate';

const PREFIX: Array<[string, SchemaName]> = [
  ['lookup.', 'lookup'],
  ['plan.', 'plan'],
  ['result.', 'result'],
  ['platform.', 'platform'],
  ['migrate-result.', 'migrate'],
];

function schemaFor(file: string): SchemaName {
  const hit = PREFIX.find(([p]) => file.startsWith(p));
  if (!hit) throw new Error(`fixture ${file} has no known prefix`);
  return hit[1];
}

const list = (kind: 'valid' | 'invalid') => fs.readdirSync(path.join(CONTRACTS, 'fixtures', kind)).filter((f) => f.endsWith('.json'));
const load = (kind: string, f: string) => JSON.parse(fs.readFileSync(path.join(CONTRACTS, 'fixtures', kind, f), 'utf8'));

describe('contract fixtures (shared with the C# bot)', () => {
  it('has valid and invalid fixtures for every schema', () => {
    for (const [, name] of PREFIX) {
      expect(list('valid').some((f) => schemaFor(f) === name)).toBe(true);
      expect(list('invalid').some((f) => schemaFor(f) === name)).toBe(true);
    }
  });

  it.each(list('valid'))('valid/%s validates', (file) => {
    const r = isValid(schemaFor(file), load('valid', file));
    expect(r.errors).toBe('');
    expect(r.ok).toBe(true);
  });

  it.each(list('invalid'))('invalid/%s is refused', (file) => {
    expect(isValid(schemaFor(file), load('invalid', file)).ok).toBe(false);
  });
});

/** The TS types are hand-written; this is the "generated types are stale" check (§13.11.2). */
describe('TypeScript mirror is in step with the schemas', () => {
  const props = (s: unknown): string[] => Object.keys((s as { properties: Record<string, unknown> }).properties).sort();
  const defs = (name: SchemaName) => (schemas[name] as unknown as { $defs: Record<string, unknown> }).$defs;
  const sorted = (k: readonly string[]) => [...k].sort();

  it.each([
    ['lookup document', () => props(schemas.lookup), KEYS.lookup],
    ['record', () => props(defs('lookup').record), KEYS.record],
    ['candidate', () => props(defs('lookup').candidate), KEYS.candidate],
    ['interfaceDiff', () => props(defs('lookup').interfaceDiff), KEYS.interfaceDiff],
    ['platform record', () => props(schemas.platform), KEYS.platform],
    ['plan', () => props(schemas.plan), KEYS.plan],
    ['plan item', () => props(defs('plan').item), KEYS.planItem],
    ['platform plan', () => props(defs('plan').platform), KEYS.platformPlan],
    ['apply result', () => props(schemas.result), KEYS.result],
    ['group result', () => props((schemas.result as unknown as { properties: { groups: { items: unknown } } }).properties.groups.items), KEYS.groupResult],
    ['migrate result', () => props(schemas.migrate), KEYS.migrate],
  ] as const)('%s', (_name, fromSchema, keys) => {
    expect(fromSchema()).toEqual(sorted(keys));
  });

  it('every schema pins contractVersion 1', () => {
    for (const name of ['lookup', 'plan', 'result', 'platform', 'migrate'] as const) {
      expect((schemas[name] as unknown as { properties: { contractVersion: { const: number } } }).properties.contractVersion.const).toBe(1);
    }
  });
});
