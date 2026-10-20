// The back-office side of door devices (commerce #521 M15, PRD §4.11): what the registration form sends and what the
// staff member copies onto the device. Pure; the spec is scanner-devices.spec.ts.

import { SparkScannerDeviceRegistered, SparkScannerDeviceRequest } from '../generated/contracts';

/** The registration form's values. */
export interface ScannerDeviceForm {
  name: string;
  listId?: string | null;
  dateId?: string | null;
}

/**
 * The request for `POST …/scanner/devices`, or `null` while the form is incomplete (no event or no device name).
 * Empty list and date choices are sent as `null` (the server's defaults: the event's list, every date).
 */
export function scannerDeviceRequest(eventId: string | null | undefined, form: ScannerDeviceForm): SparkScannerDeviceRequest | null {
  const event = eventId?.trim();
  const name = form.name.trim();
  if (!event || !name) return null;
  return { eventId: event, name, listId: form.listId?.trim() || null, dateId: form.dateId?.trim() || null };
}

/** The device key the door scanner page asks for once: `{deviceKey}.{token}` (the `X-Spark-Scanner` header value). */
export function scannerDeviceCredential(registered: Pick<SparkScannerDeviceRegistered, 'deviceKey' | 'token'>): string {
  return `${registered.deviceKey}.${registered.token}`;
}
