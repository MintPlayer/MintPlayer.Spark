import { InjectionToken, Provider, Type } from '@angular/core';

/**
 * A component that draws one kind of tile. It declares `tile = input.required<SparkBoardTile>()` and
 * `context = input.required<SparkTileContext>()`, and fetches its own content through the existing
 * Spark endpoints, so row-level security and rights apply as everywhere else.
 */
export interface SparkTileRendererRegistration {
  kind: string;
  component: Type<unknown>;
}

/** Multi-provider: tile renderers by kind. The last registration of a kind wins. */
export const SPARK_TILE_RENDERERS = new InjectionToken<SparkTileRendererRegistration[]>('SparkTileRenderers');

/** Registers tile renderers (multi — every call adds; a later kind replaces an earlier one). */
export function provideSparkTileRenderers(renderers: Record<string, Type<unknown>>): Provider[] {
  return Object.entries(renderers).map(([kind, component]) => ({
    provide: SPARK_TILE_RENDERERS,
    useValue: { kind, component } satisfies SparkTileRendererRegistration,
    multi: true,
  }));
}

/** The renderer per kind, last registration winning. */
export function tileRendererMap(registrations: readonly SparkTileRendererRegistration[] | null): Map<string, Type<unknown>> {
  const map = new Map<string, Type<unknown>>();
  for (const r of registrations ?? []) map.set(r.kind, r.component);
  return map;
}
