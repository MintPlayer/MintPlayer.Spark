import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkQueryRefreshService } from './query-refresh.service';

describe('SparkQueryRefreshService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('counts the refreshes asked for a key', () => {
    const refresh = TestBed.inject(SparkQueryRefreshService);

    refresh.request('company-people');
    refresh.request('company-people');

    expect(refresh.tokenFor('company-people')).toBe(2);
    expect(refresh.tokenFor('other')).toBe(0);
    expect(refresh.tokenFor(undefined)).toBe(0);
  });

  /** #319: the server resolves a query id or alias case-insensitively, so the refresh must too. */
  it('matches keys case-insensitively', () => {
    const refresh = TestBed.inject(SparkQueryRefreshService);

    refresh.request('Company-People');
    refresh.request('4E13A000-0000-4000-8000-000000000001');

    expect(refresh.tokenFor('company-people')).toBe(1);
    expect(refresh.tokenFor('4e13a000-0000-4000-8000-000000000001')).toBe(1);
  });
});
