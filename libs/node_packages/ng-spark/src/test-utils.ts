import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router } from '@angular/router';
import { filter, firstValueFrom } from 'rxjs';

/**
 * Empty standalone component for use as a destination route in RouterTestingHarness setups.
 * Avoids per-spec re-declaration when only navigation outcomes are asserted.
 */
@Component({ standalone: true, template: '' })
export class StubComponent {}

/**
 * Resolves with the next NavigationEnd Router emits.
 *
 * Components in this codebase fire-and-forget `router.navigate*(...)` without awaiting
 * the returned Promise, so a method that triggers navigation resolves before the URL
 * actually changes. Subscribe to this BEFORE calling the trigger:
 *
 *   const navigated = nextNavigationEnd();
 *   await component.onSave();
 *   await navigated;
 *   expect(TestBed.inject(Router).url).toBe(...);
 *
 * Reliable across zoneless mode where harness.fixture.whenStable() alone wasn't enough.
 */
export function nextNavigationEnd(): Promise<NavigationEnd> {
  const router = TestBed.inject(Router);
  return firstValueFrom(
    router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd)),
  );
}
