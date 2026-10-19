/**
 * One rendering's zone (commerce PRD X16), the wire form of the .NET `SparkDateTimeZone`:
 * the viewer's zone, the stored wall clock with its offset, a fixed IANA zone, or the IANA zone held by
 * another attribute of the same object.
 */
export type SparkDateTimeZone = 'viewer' | 'stored' | { zone: string } | { zoneFrom: string };

/**
 * An attribute's `dateTimeDisplay` (X16, D34): the zone of its main rendering (`primary`, the viewer's by
 * default) and an optional second rendering (`hint`), hidden when both give the same wall clock at that
 * instant. Rendered by `<spark-date-time>` in grids and on detail pages.
 *
 * ```json
 * "dateTimeDisplay": { "primary": "viewer", "hint": { "zoneFrom": "VenueTimeZoneId" } }
 * ```
 */
export interface SparkDateTimeDisplay {
  primary?: SparkDateTimeZone | null;
  hint?: SparkDateTimeZone | null;
}
