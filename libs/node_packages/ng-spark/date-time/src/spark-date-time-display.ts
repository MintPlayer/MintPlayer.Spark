import { Pipe, PipeTransform } from '@angular/core';
import { SparkDateTimeDisplay, SparkDateTimeZone, SparkRow, valueFor } from '@mintplayer/ng-spark/models';
import { SparkDateTimeMode } from './spark-date-time-format';

/** An attribute's `dateTimeDisplay` resolved against one row: what `<spark-date-time [mode] [alsoIn]>` takes. */
export interface SparkResolvedDateTimeDisplay {
  mode: SparkDateTimeMode;
  alsoIn: string | null;
}

const VIEWER: SparkResolvedDateTimeDisplay = { mode: 'viewer', alsoIn: null };

/**
 * Resolves a model rule (X16) against the row it renders on. A `zoneFrom` reads the named attribute of the
 * same row (a query row, a persistent object or an AsDetail record); when that attribute is absent or
 * empty, the main rendering is the viewer's and the hint is left out.
 */
export function resolveSparkDateTimeDisplay(
  rule: SparkDateTimeDisplay | null | undefined,
  row: SparkRow | null | undefined,
): SparkResolvedDateTimeDisplay {
  if (!rule) return VIEWER;

  const zoneOf = (spec: SparkDateTimeZone | null | undefined): string | 'stored' | null => {
    if (!spec || spec === 'viewer') return null;
    if (spec === 'stored') return 'stored';
    if ('zone' in spec) return spec.zone || null;
    const held = valueFor(row, spec.zoneFrom)?.value;
    return typeof held === 'string' && held.trim() ? held.trim() : null;
  };

  const primary = zoneOf(rule.primary);
  return {
    mode: primary === null ? 'viewer' : primary === 'stored' ? 'stored' : { zone: primary },
    alsoIn: zoneOf(rule.hint),
  };
}

/** `@let display = attr.dateTimeDisplay | sparkDateTimeDisplay:row;` — {@link resolveSparkDateTimeDisplay} in a template. */
@Pipe({ name: 'sparkDateTimeDisplay', standalone: true, pure: true })
export class SparkDateTimeDisplayPipe implements PipeTransform {
  transform(rule: SparkDateTimeDisplay | null | undefined, row: SparkRow | null | undefined): SparkResolvedDateTimeDisplay {
    return resolveSparkDateTimeDisplay(rule, row);
  }
}
