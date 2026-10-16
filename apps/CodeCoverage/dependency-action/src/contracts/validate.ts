import Ajv2020, { type ErrorObject, type ValidateFunction } from 'ajv/dist/2020';
import migrateSchema from '../../../contracts/dependency-update/migrate-result.schema.json';
import planSchema from '../../../contracts/dependency-update/plan.schema.json';
import platformSchema from '../../../contracts/dependency-update/platform-record.schema.json';
import recordSchema from '../../../contracts/dependency-update/record.schema.json';
import resultSchema from '../../../contracts/dependency-update/result.schema.json';

export const schemas = {
  lookup: recordSchema,
  plan: planSchema,
  result: resultSchema,
  platform: platformSchema,
  migrate: migrateSchema,
} as const;

export type SchemaName = keyof typeof schemas;

let ajv: Ajv2020 | undefined;
const compiled = new Map<SchemaName, ValidateFunction>();

function instance(): Ajv2020 {
  if (!ajv) {
    ajv = new Ajv2020({ allErrors: true, strict: true, allowUnionTypes: true });
    for (const schema of Object.values(schemas)) ajv.addSchema(schema as object);
  }
  return ajv;
}

export function validator(name: SchemaName): ValidateFunction {
  let fn = compiled.get(name);
  if (!fn) {
    fn = instance().getSchema((schemas[name] as { $id: string }).$id)!;
    compiled.set(name, fn);
  }
  return fn;
}

export function formatErrors(errors: ErrorObject[] | null | undefined): string {
  return (errors ?? []).map((e) => `${e.instancePath || '/'} ${e.message ?? ''}`.trim()).join('; ');
}

export class ContractError extends Error {
  constructor(name: SchemaName, detail: string) {
    super(`${name} does not match its contract: ${detail}`);
    this.name = 'ContractError';
  }
}

/** Throws ContractError when `value` does not satisfy the named schema. Every emitted JSON goes through this. */
export function assertValid<T>(name: SchemaName, value: unknown): T {
  const fn = validator(name);
  if (!fn(value)) throw new ContractError(name, formatErrors(fn.errors));
  return value as T;
}

export function isValid(name: SchemaName, value: unknown): { ok: boolean; errors: string } {
  const fn = validator(name);
  const ok = fn(value) as boolean;
  return { ok, errors: ok ? '' : formatErrors(fn.errors) };
}
