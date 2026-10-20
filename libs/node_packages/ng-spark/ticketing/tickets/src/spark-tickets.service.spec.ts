import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { resaleCurrency, ticketCredentialFromLink, ticketRefusalKey, ticketRefusalOf, transferTokenFromLink } from './spark-tickets.service';
import { scannerErrorOf } from './scanner/scanner-client';

// Commerce #521 M15: the ng-spark client helpers of MintPlayer.Spark.Commerce.Ticketing.Tickets.

describe('ticket links', () => {
  it('read the secret and the transfer token from the fragment only', () => {
    expect(ticketCredentialFromLink('0123456789abcdef', '#k=abcdefghijklmnopqrstuv')).toEqual({ code: '0123456789ABCDEF', secret: 'abcdefghijklmnopqrstuv' });
    expect(ticketCredentialFromLink('0123456789ABCDEF', '')).toEqual({ code: '0123456789ABCDEF', secret: null });
    expect(transferTokenFromLink('#t=tok_en-1')).toBe('tok_en-1');
    expect(transferTokenFromLink(null)).toBeNull();
  });
});

describe('ticket refusals', () => {
  it('read a 409 envelope and key own and checkout codes', () => {
    expect(ticketRefusalOf(new HttpErrorResponse({ status: 409, error: { result: { code: 'frozen' } } }))?.code).toBe('frozen');
    expect(ticketRefusalOf(new HttpErrorResponse({ status: 404 }))).toBeNull();
    expect(ticketRefusalKey('frozen')).toBe('ticketingTickets.refusal.frozen');
    expect(ticketRefusalKey('soldOut')).toBe('commerceCheckout.refusal.soldOut');
  });
});

describe('scanner errors', () => {
  it('tell offline, revoked and unauthorized apart', () => {
    expect(scannerErrorOf(new HttpErrorResponse({ status: 0 })).kind).toBe('offline');
    expect(scannerErrorOf(new HttpErrorResponse({ status: 503 })).kind).toBe('offline');
    expect(scannerErrorOf(new HttpErrorResponse({ status: 401 })).kind).toBe('unauthorized');
    expect(scannerErrorOf(new HttpErrorResponse({ status: 409, error: { result: { code: 'deviceRevoked' } } })).kind).toBe('revoked');
    expect(scannerErrorOf(new HttpErrorResponse({ status: 409, error: { result: { code: 'tooManyScans' } } }))).toMatchObject({ kind: 'other', code: 'tooManyScans' });
    expect(scannerErrorOf(new TypeError('Failed to fetch')).kind).toBe('offline');
  });
});

describe('resale currency', () => {
  // The resale form must list in the currency the event's order was paid in, never a default (it was 'EUR').
  const cap = (Currency: string) => ({ Amount: 30, Currency });
  it('takes the open listing first, else the ceiling the server sends before any listing exists', () => {
    expect(resaleCurrency({ resale: { listingId: 'L', status: 'Open', price: cap('GBP'), cap: cap('GBP') }, resaleCap: cap('GBP') })).toBe('GBP');
    expect(resaleCurrency({ resale: null, resaleCap: cap('CHF') })).toBe('CHF');
  });
  it('knows no currency without a listing or a ceiling, instead of guessing EUR', () => {
    expect(resaleCurrency({ resale: null, resaleCap: null })).toBeNull();
    expect(resaleCurrency(null)).toBeNull();
  });
});
