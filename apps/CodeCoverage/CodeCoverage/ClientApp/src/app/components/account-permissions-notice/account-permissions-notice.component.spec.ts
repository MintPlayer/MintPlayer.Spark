import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it } from 'vitest';
import { PendingInstallationPermissions } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { AccountPermissionsNoticeComponent } from './account-permissions-notice.component';

describe('AccountPermissionsNoticeComponent', () => {
  let fixture: ComponentFixture<AccountPermissionsNoticeComponent>;
  let browse: BrowseServiceStub;

  async function create(getPendingPermissions: () => Promise<PendingInstallationPermissions | null>) {
    browse = createBrowseStub({ getPendingPermissions });
    TestBed.configureTestingModule({
      imports: [AccountPermissionsNoticeComponent],
      providers: [provideZonelessChangeDetection(), provideNoopAnimations(), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(AccountPermissionsNoticeComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('login', 'acme');
    await settle(fixture);
  }

  const link = () => fixture.nativeElement.querySelector('a') as HTMLAnchorElement | null;

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
