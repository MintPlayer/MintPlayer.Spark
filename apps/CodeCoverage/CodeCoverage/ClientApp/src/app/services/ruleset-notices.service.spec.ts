import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { RulesetNoticesService } from './ruleset-notices.service';

describe('RulesetNoticesService', () => {
  const signedOut = { isAuthenticated: false };

  function setup(initial: unknown) {
    const authState = signal<unknown>(initial);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_STATE, useValue: authState },
      ],
    });
    const service = TestBed.inject(RulesetNoticesService);
    const controller = TestBed.inject(HttpTestingController);
    service.start();
    TestBed.tick();
    return { authState, service, controller };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('asks for the notices once when the app opens signed in', () => {
    const { authState, controller } = setup({ userName: 'ann' });

    const req = controller.expectOne('/api/me/ruleset-notices');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ delivered: 1 });

    // A refreshed user object that stays signed in asks nothing more.
    authState.set({ userName: 'ann', roles: ['x'] });
    TestBed.tick();
    controller.expectNone('/api/me/ruleset-notices');
  });

  it('asks nothing while signed out, and asks once the user signs in', () => {
    const { authState, controller } = setup(signedOut);
    controller.expectNone('/api/me/ruleset-notices');

    authState.set({ userName: 'ann' });
    TestBed.tick();
    controller.expectOne('/api/me/ruleset-notices').flush({ delivered: 0 });

    authState.set(signedOut);
    TestBed.tick();
    authState.set({ userName: 'bob' });
    TestBed.tick();
    controller.expectOne('/api/me/ruleset-notices').flush({ delivered: 2 });
  });

  it('starts following only once', () => {
    const { service, controller } = setup({ userName: 'ann' });
    service.start();
    TestBed.tick();

    controller.expectOne('/api/me/ruleset-notices').flush({ delivered: 0 });
  });

  it('resolves 0 when the request fails, so the next sign-in simply asks again', async () => {
    const { service, controller } = setup(signedOut);

    const result = service.deliver();
    controller.expectOne('/api/me/ruleset-notices').flush('nope', { status: 500, statusText: 'Server Error' });

    expect(await result).toBe(0);
  });
});
