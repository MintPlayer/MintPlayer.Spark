import { EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import { SPARK_TIME_ZONE_NAME_SOURCE, SparkTimeZoneNameSource } from '@mintplayer/ng-spark/date-time';
import { ReferenceDataApi } from './reference-data-api.service';
import { REFERENCE_DATA_CONFIG, ReferenceDataConfig, normalizeBaseUrl } from './reference-data.token';

/**
 * Wires the reference-data client: the API base URL and the time-zone name source (D36 step 1: CLDR exemplar
 * cities, "20:00 in Brussel").
 *
 * @example
 * provideReferenceData({ baseUrl: 'https://refdata.example.org' }),   // null = free-text mode; no default URL
 * provideSparkAttributeRenderers([...sparkAddressRenderers]),
 */
export function provideReferenceData(config: ReferenceDataConfig): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: REFERENCE_DATA_CONFIG, useValue: { baseUrl: normalizeBaseUrl(config.baseUrl) } },
    {
      provide: SPARK_TIME_ZONE_NAME_SOURCE,
      useFactory: (): SparkTimeZoneNameSource => {
        const api = inject(ReferenceDataApi);
        return { names: language => api.timeZoneNames(language) };
      },
    },
  ]);
}
