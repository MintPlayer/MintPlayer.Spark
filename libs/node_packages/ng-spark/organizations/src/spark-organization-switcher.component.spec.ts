import { HttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkCurrentOrganization, SparkOrganizationSwitcherComponent } from './spark-organization-switcher.component';

const two: SparkCurrentOrganization = {
  current: null,
  organizations: [
    { id: 'Organizations/personal/u1', name: 'Ann', isPersonal: true },
    { id: 'Organizations/a', name: 'Acme', isPersonal: false },
  ],
};

function configure(get: ReturnType<typeof vi.fn>, post: ReturnType<typeof vi.fn> = vi.fn()) {
  TestBed.configureTestingModule({
    imports: [SparkOrganizationSwitcherComponent],
    providers: [
      provideNoopAnimations(),
      { provide: HttpClient, useValue: { get, post } },
      { provide: SparkLanguageService, useValue: { t: (k: string, fallback?: string) => fallback ?? k } },
    ],
  });
  const fixture = TestBed.createComponent(SparkOrganizationSwitcherComponent);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const reload = vi.spyOn(fixture.componentInstance as any, 'reload').mockImplementation(() => undefined);
  return { fixture, reload };
}

describe('SparkOrganizationSwitcherComponent', () => {
  it('offers All organizations and each organization, the personal one marked', async () => {
    const { fixture } = configure(vi.fn().mockReturnValue(of(two)));
    await fixture.componentInstance.ngOnInit();
    fixture.detectChanges();

    const options = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('option')).map(o => o.textContent?.trim());
    expect(options).toEqual(['All organizations', 'Ann (personal)', 'Acme']);
  });

  it('renders nothing for a user in one organization, or when the server answers no switcher', async () => {
    const one = configure(vi.fn().mockReturnValue(of({ current: null, organizations: [two.organizations[0]] })));
    await one.fixture.componentInstance.ngOnInit();
    one.fixture.detectChanges();
    expect((one.fixture.nativeElement as HTMLElement).querySelector('bs-select')).toBeNull();

    TestBed.resetTestingModule();
    const signedOut = configure(vi.fn().mockReturnValue(throwError(() => new Error('401'))));
    await signedOut.fixture.componentInstance.ngOnInit();
    signedOut.fixture.detectChanges();
    expect(signedOut.fixture.componentInstance.state()).toBeNull();
    expect((signedOut.fixture.nativeElement as HTMLElement).querySelector('bs-select')).toBeNull();
  });

  it('posts the choice and reloads, so every list reads the new scope', async () => {
    const post = vi.fn().mockReturnValue(of({ ...two, current: 'Organizations/a' }));
    const { fixture, reload } = configure(vi.fn().mockReturnValue(of(two)), post);
    await fixture.componentInstance.ngOnInit();

    await fixture.componentInstance.choose('Organizations/a');

    expect(post).toHaveBeenCalledWith('/spark/organizations/current', { organizationId: 'Organizations/a' });
    expect(fixture.componentInstance.state()?.current).toBe('Organizations/a');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('does nothing for the current choice, and re-reads the server state when a choice is refused', async () => {
    const get = vi.fn().mockReturnValue(of(two));
    const post = vi.fn().mockReturnValue(throwError(() => new Error('404')));
    const { fixture, reload } = configure(get, post);
    await fixture.componentInstance.ngOnInit();

    await fixture.componentInstance.choose(null);
    expect(post).not.toHaveBeenCalled();

    await fixture.componentInstance.choose('Organizations/gone');
    expect(reload).not.toHaveBeenCalled();
    expect(get).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.busy()).toBe(false);
  });
});
