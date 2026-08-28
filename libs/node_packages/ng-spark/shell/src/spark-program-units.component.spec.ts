import { Component, computed, signal, TemplateRef, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkProgramUnitsComponent } from './spark-program-units.component';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import { ProgramUnitsConfiguration } from '@mintplayer/ng-spark/models';

/**
 * The menu's contract: everything rendered comes from the server response (consumers write no
 * router links), both levels are sorted by `order`, and the caller-scoped response is re-fetched
 * whenever the auth signal or the reload token changes.
 */

const config: ProgramUnitsConfiguration = {
  programUnitGroups: [
    {
      id: 'g2', name: { en: 'Second' }, order: 2,
      programUnits: [
        { id: 'u-url', name: { en: 'Status' }, type: 'url', url: 'https://status.example.com', order: 1 },
      ],
    },
    {
      id: 'g1', name: { en: 'First' }, order: 1,
      programUnits: [
        { id: 'u-b', name: { en: 'B' }, type: 'query', queryId: 'q-b', alias: 'bees', order: 2 },
        { id: 'u-a', name: { en: 'A' }, type: 'persistentObject', persistentObjectId: 'po-a', alias: 'start-page', objectId: 'start', order: 1 },
      ],
    },
  ],
};

/** Includes a macrotask turn so the accordion's custom-element registration lands while the
 *  fixture is still alive — see the note on the same helper in spark-shell.component.spec.ts. */
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  await new Promise(resolve => setTimeout(resolve, 0));
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('SparkProgramUnitsComponent', () => {
  const getProgramUnits = vi.fn(async () => config);

  beforeEach(() => {
    getProgramUnits.mockClear();
    TestBed.configureTestingModule({
      imports: [SparkProgramUnitsComponent],
      providers: [
        provideRouter([]),
        { provide: SparkService, useValue: { getProgramUnits } },
      ],
    });
  });

  it('renders groups and units sorted by order, links sourced from the response', async () => {
    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture);

    const el: HTMLElement = fixture.nativeElement;
    const anchors = Array.from(el.querySelectorAll('a'));
    // g1 (order 1) before g2 (order 2); within g1, u-a (order 1) before u-b (order 2).
    expect(anchors.map(a => a.textContent?.trim())).toEqual(['A', 'B', 'Status']);
    // Deep link: alias + objectId.
    expect(anchors[0].getAttribute('href')).toBe('/po/start-page/start');
    expect(anchors[1].getAttribute('href')).toBe('/query/bees');
  });

  it('renders a url unit as an external anchor, not a router link', async () => {
    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture);

    const external = Array.from(fixture.nativeElement.querySelectorAll('a'))
      .find((a: any) => a.getAttribute('href') === 'https://status.example.com') as HTMLAnchorElement;
    expect(external).toBeTruthy();
    expect(external.getAttribute('target')).toBe('_blank');
    expect(external.getAttribute('rel')).toBe('noopener');
  });

  it('renders extraTabs after the generated groups, inside the one accordion', async () => {
    @Component({
      standalone: true,
      imports: [SparkProgramUnitsComponent],
      template: `
        <ng-template #extra><a class="extra-link">Query card slots</a></ng-template>
        <spark-program-units [extraTabs]="tabs()" />`,
    })
    class Host {
      readonly extra = viewChild<TemplateRef<unknown>>('extra');
      readonly tabs = computed(() => {
        const content = this.extra();
        return content ? [{ header: 'Component demos', content }] : [];
      });
    }

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle(fixture);

    const el: HTMLElement = fixture.nativeElement;
    const accordions = el.querySelectorAll('bs-accordion');
    expect(accordions.length).toBe(1);

    const tabs = accordions[0].querySelectorAll('bs-accordion-tab');
    expect(tabs.length).toBe(3);                              // two server groups, then the extra
    expect(tabs[2].querySelector('.extra-link')).toBeTruthy();
    expect(el.textContent).toContain('Component demos');
  });

  it('re-fetches when the SPARK_AUTH_STATE signal changes (sign-in/out)', async () => {
    const authState = signal<unknown>(null);
    TestBed.overrideProvider(SPARK_AUTH_STATE, { useValue: authState.asReadonly() });

    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture);
    expect(getProgramUnits).toHaveBeenCalledTimes(1);

    authState.set({ userName: 'alice' });
    await settle(fixture);
    expect(getProgramUnits).toHaveBeenCalledTimes(2);
  });

  it('fetches exactly once without an auth provider, and again on reloadToken change', async () => {
    @Component({
      standalone: true,
      imports: [SparkProgramUnitsComponent],
      template: `<spark-program-units [reloadToken]="token()" />`,
    })
    class Host {
      readonly token = signal(0);
    }

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle(fixture);
    expect(getProgramUnits).toHaveBeenCalledTimes(1);

    fixture.componentInstance.token.set(1);
    await settle(fixture);
    expect(getProgramUnits).toHaveBeenCalledTimes(2);
  });
});
