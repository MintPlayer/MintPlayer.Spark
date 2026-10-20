import { EnvironmentProviders, Injectable, InjectionToken, Signal, WritableSignal, inject, makeEnvironmentProviders, signal } from '@angular/core';
import { sparkGenericZoneName, sparkTimeZoneCity } from './spark-date-time-format';

/**
 * Step 1 of the zone-label fallback (D36): translated zone names from a data source, typically the
 * reference-data application's CLDR exemplar cities ("Brussel"). Its client package provides one with
 * {@link provideSparkTimeZoneNameSource} (commerce plan M19). Asked once per language per session.
 */
export interface SparkTimeZoneNameSource {
  /** IANA id → city name in `language`; missing zones fall back. */
  names(language: string): Promise<Record<string, string>>;
}

export const SPARK_TIME_ZONE_NAME_SOURCE = new InjectionToken<SparkTimeZoneNameSource>('SPARK_TIME_ZONE_NAME_SOURCE');

/** Registers the translated zone-name source (step 1 of the label fallback). */
export function provideSparkTimeZoneNameSource(factory: () => SparkTimeZoneNameSource): EnvironmentProviders {
  return makeEnvironmentProviders([{ provide: SPARK_TIME_ZONE_NAME_SOURCE, useFactory: factory }]);
}

/**
 * The client twin of the server's `ISparkTimeZoneNames` (D36): the label of a zone in a language, through
 * the fallback chain
 *
 * 1. the {@link SparkTimeZoneNameSource}, when one is provided: the translated city;
 * 2. the platform-localized generic name (`Intl` `longGeneric`, "Midden-Europese tijd"; S23 measured it
 *    localized per call in Chromium, Firefox and Node);
 * 3. the English city from the IANA id.
 *
 * Rendering never waits: {@link label} answers a signal holding the best fallback at once, and the source's
 * city replaces it when it arrives. Cached per (zone, language) for the session.
 */
@Injectable({ providedIn: 'root' })
export class SparkTimeZoneNames {
  private readonly source = inject(SPARK_TIME_ZONE_NAME_SOURCE, { optional: true });
  private readonly labels = new Map<string, WritableSignal<string>>();
  private readonly languages = new Map<string, Promise<Record<string, string>>>();

  /** The label of `zone` in `language`. The same signal for the same pair; it may change once, when the source answers. */
  label(zone: string, language: string): Signal<string> {
    const key = `${zone}|${language}`;
    let label = this.labels.get(key);
    if (!label) {
      label = signal(sparkGenericZoneName(zone, language) ?? sparkTimeZoneCity(zone));
      this.labels.set(key, label);
      this.fromSource(zone, language, label);
    }
    return label.asReadonly();
  }

  private fromSource(zone: string, language: string, label: WritableSignal<string>): void {
    if (!this.source) return;
    let names = this.languages.get(language);
    if (!names) {
      // One request per language per session; a failure is remembered as "no names" rather than retried per label.
      names = this.source.names(language).catch((): Record<string, string> => ({}));
      this.languages.set(language, names);
    }
    void names.then(map => {
      const city = map[zone];
      if (city) label.set(city);
    });
  }
}
