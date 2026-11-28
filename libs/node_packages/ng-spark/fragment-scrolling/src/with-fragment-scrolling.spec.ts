import { ViewportScroller } from '@angular/common';
import { APP_BOOTSTRAP_LISTENER, ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Routes, provideRouter, withInMemoryScrolling } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { provideSpark } from '@mintplayer/ng-spark';
import { SparkViewportScroller } from './spark-viewport-scroller';
import { withFragmentScrolling } from './with-fragment-scrolling';

/**
 * #721, Part E and Dec11: `withFragmentScrolling()` puts Spark's scroller where the router's anchor scrolling calls it,
 * asks for the first anchor itself when the router did not (a hydrating app), and warns in dev mode when a later
 * fragment navigation never asks (the app forgot `anchorScrolling`).
 */
@Component({ template: '<section id="career">Career</section>' })
class PageComponent {}

const routes: Routes = [
  { path: '', component: PageComponent },
  { path: 'other', component: PageComponent },
];

async function setup(anchorScrolling: boolean) {
  TestBed.configureTestingModule({
    providers: [
      anchorScrolling
        ? provideRouter(routes, withInMemoryScrolling({ anchorScrolling: 'enabled' }))
        : provideRouter(routes),
      provideSpark(withFragmentScrolling()),
    ],
  });
  const scroller = TestBed.inject(SparkViewportScroller);
  const asked = vi.spyOn(scroller, 'scrollToAnchor');
  const harness = await RouterTestingHarness.create();
  if (anchorScrolling) {
    // The router starts its scroller in its bootstrap listener, which TestBed never runs: run it as a bootstrap would.
    // It also makes the initial navigation, the app's first.
    const appRef = TestBed.inject(ApplicationRef);
    for (const listener of TestBed.inject(APP_BOOTSTRAP_LISTENER))
      listener(appRef.components[0]);
    await harness.fixture.whenStable();
  }
  return { harness, scroller, asked };
}

describe('withFragmentScrolling (#721)', () => {
  afterEach(() => vi.restoreAllMocks());

  it('replaces the ViewportScroller the router uses', async () => {
    const { scroller } = await setup(true);
    expect(TestBed.inject(ViewportScroller)).toBe(scroller);
  });

  it('is asked for the anchor of a fragment navigation', async () => {
    const { harness, asked } = await setup(true);

    await harness.navigateByUrl('/');
    await harness.navigateByUrl('/other#career');

    await vi.waitFor(() => expect(asked).toHaveBeenCalledWith('career'));
  });

  it('is asked again when only the fragment changes', async () => {
    const { harness, asked } = await setup(true);

    await harness.navigateByUrl('/other#top');
    await vi.waitFor(() => expect(asked).toHaveBeenCalledWith('top'));
    await harness.navigateByUrl('/other#career');

    await vi.waitFor(() => expect(asked).toHaveBeenCalledWith('career'));
  });

  it('is asked again for the same fragment: following the link twice scrolls back to it', async () => {
    const { harness, asked } = await setup(true);

    await harness.navigateByUrl('/other#career');
    await vi.waitFor(() => expect(asked).toHaveBeenCalledTimes(1));
    // The same URL again is skipped by the router (NavigationSkipped), which still asks for the anchor.
    await harness.navigateByUrl('/other#career');

    await vi.waitFor(() => expect(asked).toHaveBeenCalledTimes(2));
  });

  it('asks for the first anchor itself when the router did not (hydration)', async () => {
    const { harness, asked } = await setup(false);

    await harness.navigateByUrl('/#career');

    await vi.waitFor(() => expect(asked).toHaveBeenCalledWith('career'));
    expect(asked).toHaveBeenCalledTimes(1);
  });

  it('warns in dev mode when a fragment navigation never asks for its anchor', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { harness } = await setup(false);

    await harness.navigateByUrl('/');
    await harness.navigateByUrl('/other#career');

    await vi.waitFor(() => expect(warn).toHaveBeenCalledWith(expect.stringContaining("anchorScrolling: 'enabled'")), { timeout: 2000 });
  });

  it('does not warn when the router asks', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { harness, asked } = await setup(true);

    await harness.navigateByUrl('/');
    await harness.navigateByUrl('/other#career');
    await vi.waitFor(() => expect(asked).toHaveBeenCalled());
    await new Promise(resolve => setTimeout(resolve, 1200));

    expect(warn).not.toHaveBeenCalledWith(expect.stringContaining('anchorScrolling'));
  });
});
