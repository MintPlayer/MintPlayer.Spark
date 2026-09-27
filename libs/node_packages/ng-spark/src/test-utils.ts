import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
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

/**
 * Settles a fixture: drains the change-detection / microtask queue until quiet.
 *
 * Components here often await more than once (query, then entity types, then permissions), so a
 * single `whenStable()` returns while the second level is still pending. `rounds` bounds the drain.
 *
 * `macrotask: true` adds a final task turn. Needed by anything that registers a Lit custom element
 * from `afterNextRender` (bs-shell, bs-accordion, the charts): ending the test before that turn tears
 * the fixture down with the registration in flight, and jsdom then throws
 * `Cannot read properties of null (reading '_namespaceURI')` from an unhandled rejection that fails
 * the run while every assertion passes.
 */
export async function settle(
  fixture: ComponentFixture<unknown>,
  options: { rounds?: number; macrotask?: boolean } = {},
): Promise<void> {
  const { rounds = 5, macrotask = false } = options;
  for (let i = 0; i < rounds; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  if (macrotask) {
    await new Promise(resolve => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  } else {
    await fixture.whenStable();
  }
}
