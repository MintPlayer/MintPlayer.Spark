import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { PendingInstallationPermissions } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { AccountPermissionsNoticeComponent } from './account-permissions-notice.component';

const pending: PendingInstallationPermissions = {
  htmlUrl: 'https://github.com/organizations/acme/settings/installations/42',
  pending: [{ name: 'actions', displayName: 'Actions', declared: 'write', accepted: null }],
};

describe('AccountPermissionsNoticeComponent', () => {
  let fixture: ComponentFixture<AccountPermissionsNoticeComponent>;
  let browse: BrowseServiceStub;

  async function create(getPendingPermissions: () => Promise<PendingInstallationPermissions | null>, showGuideLink?: boolean) {
    browse = createBrowseStub({ getPendingPermissions });
    TestBed.configureTestingModule({
      imports: [AccountPermissionsNoticeComponent],
      providers: [provideZonelessChangeDetection(), provideNoopAnimations(), provideRouter([]), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(AccountPermissionsNoticeComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('login', 'acme');
    if (showGuideLink !== undefined) fixture.componentRef.setInput('showGuideLink', showGuideLink);
    await settle(fixture);
  }

  const link = () => fixture.nativeElement.querySelector('a') as HTMLAnchorElement | null;
  const guideLink = () => fixture.nativeElement.querySelector('[data-testid="merge-queue-setup-link"]') as HTMLAnchorElement | null;

  it('names every pending permission and links to the installation page the server returned', async () => {
    await create(() => Promise.resolve({
      htmlUrl: 'https://github.com/organizations/acme/settings/installations/42',
      pending: [
        { name: 'actions', displayName: 'Actions', declared: 'write', accepted: null },
        { name: 'workflows', displayName: 'Workflows', declared: 'write', accepted: 'read' },
      ],
    }));

    expect(browse.getPendingPermissions).toHaveBeenCalledWith('github', 'acme');
    expect(fixture.nativeElement.textContent).toContain(
      'GitHub is waiting for you to accept new permissions: Actions (write), Workflows (write).');
    expect(link()?.getAttribute('href')).toBe('https://github.com/organizations/acme/settings/installations/42');
    expect(link()?.getAttribute('target')).toBe('_blank');
    expect(link()?.getAttribute('rel')).toBe('noopener');
  });

  // The guide shows this account's notice in its first step, so the link carries the account and lands on that step.
  it('links the merge queue setup guide for this account, at its permissions step', async () => {
    await create(() => Promise.resolve(pending));
    expect(guideLink()?.getAttribute('href')).toBe('/merge-queue/setup?provider=github&account=acme#permissions');
  });

  it('leaves the guide link out where it would point at the page it is on', async () => {
    await create(() => Promise.resolve(pending), false);
    expect(link()?.getAttribute('href')).toBe('https://github.com/organizations/acme/settings/installations/42');
    expect(guideLink()).toBeNull();
  });

  it('shows nothing when nothing is pending', async () => {
    await create(() => Promise.resolve({ htmlUrl: 'https://github.com/settings/installations/42', pending: [] }));
    expect(link()).toBeNull();
  });

  it('shows nothing when the server has nothing to say (204)', async () => {
    await create(() => Promise.resolve(null));
    expect(link()).toBeNull();
  });

  it('shows nothing when the viewer does not manage the account', async () => {
    await create(() => Promise.reject({ status: 404 }));
    expect(link()).toBeNull();
  });
});
