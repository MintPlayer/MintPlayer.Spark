import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AccountsResponse, AccountsService } from './accounts.service';

describe('AccountsService', () => {
  let service: AccountsService;
  let controller: HttpTestingController;

  const body: AccountsResponse = {
    connectUrl: 'https://forge.example.test/apps/coverage',
    accounts: [{ login: 'acme', type: 'Organization', installed: true, repoCount: 3, aggregateCoverage: 81.2 }],
    reauthRequired: false,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AccountsService);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('reads the caller accounts from GET /api/me/accounts', async () => {
    const result = service.getMyAccounts();

    const req = controller.expectOne('/api/me/accounts');
    expect(req.request.method).toBe('GET');
    req.flush(body);
    expect(await result).toEqual(body);
  });

  it('resyncs with a POST to /api/me/accounts/resync and returns the fresh list', async () => {
    const result = service.resync();

    const req = controller.expectOne('/api/me/accounts/resync');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ ...body, reauthRequired: true });
    expect((await result).reauthRequired).toBe(true);
  });

  it('rejects on an HTTP error rather than resolving empty', async () => {
    const result = service.getMyAccounts();

    controller.expectOne('/api/me/accounts').flush('nope', { status: 500, statusText: 'Server Error' });
    await expect(result).rejects.toMatchObject({ status: 500 });
  });
});
