import { describe, expect, it } from 'vitest';
import { scannerDeviceCredential, scannerDeviceRequest } from './scanner-devices';

// Commerce #521 M15 (§4.11): the back-office registration of a door device.

describe('scanner device registration', () => {
  it('sends the event, the trimmed name, and null for the default list and every date', () => {
    expect(scannerDeviceRequest('TicketEvents/1', { name: '  Gate A tablet ', listId: '', dateId: '  ' })).toEqual({
      eventId: 'TicketEvents/1', name: 'Gate A tablet', listId: null, dateId: null,
    });
    expect(scannerDeviceRequest('TicketEvents/1', { name: 'Gate B', listId: 'TicketCheckInLists/vip', dateId: 'TicketEventDates/2' })).toEqual({
      eventId: 'TicketEvents/1', name: 'Gate B', listId: 'TicketCheckInLists/vip', dateId: 'TicketEventDates/2',
    });
  });

  it('waits for a name and an event before anything is sent', () => {
    expect(scannerDeviceRequest('TicketEvents/1', { name: '   ' })).toBeNull();
    expect(scannerDeviceRequest('', { name: 'Gate A' })).toBeNull();
    expect(scannerDeviceRequest(null, { name: 'Gate A' })).toBeNull();
  });

  it('shows the device key in the form the door scanner pastes: {deviceKey}.{token}', () => {
    expect(scannerDeviceCredential({ deviceKey: 'k1', token: 'secret-token' })).toBe('k1.secret-token');
  });
});
