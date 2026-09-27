import { Component, forwardRef, input, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RepoInfo } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { RepoBadgePanelComponent } from './repo-badge-panel.component';

/**
 * Stand-in for the Lit-backed `<bs-select>`: a plain value accessor, so `[ngModel]` binds and a
 * spec can read the selected value or push a change the way a user pick would.
 */
@Component({
  selector: 'bs-select',
  template: '<ng-content />',
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => StubSelect), multi: true }],
})
class StubSelect implements ControlValueAccessor {
  readonly size = input<string>();
  value: unknown;
  onChange: (value: unknown) => void = () => undefined;
  writeValue(value: unknown): void { this.value = value; }
  registerOnChange(fn: (value: unknown) => void): void { this.onChange = fn; }
  registerOnTouched(): void { /* not needed */ }
}

function repo(overrides: Partial<RepoInfo> = {}): RepoInfo {
  return {
    id: 'repos/1', owner: 'acme', name: 'widgets', fullName: 'acme/widgets', isPrivate: false,
    defaultBranch: 'main', canManage: false, baseUrl: 'https://cov.example.test', ...overrides,
  };
}

describe('RepoBadgePanelComponent', () => {
  let fixture: ComponentFixture<RepoBadgePanelComponent>;
  let component: RepoBadgePanelComponent;
  let browse: BrowseServiceStub;

  async function create(overrides: Parameters<typeof createBrowseStub>[0] = {}) {
    browse = createBrowseStub({
      getRepo: () => Promise.resolve(repo()),
      getBranches: () => Promise.resolve(['main']),
      ...overrides,
    });
    TestBed.configureTestingModule({
      imports: [RepoBadgePanelComponent],
      providers: [provideZonelessChangeDetection(), provideBrowseStub(browse)],
    });
    TestBed.overrideComponent(RepoBadgePanelComponent, {
      remove: { imports: [BsSelectComponent, BsSelectOption] },
      add: { imports: [StubSelect] },
    });
    fixture = TestBed.createComponent(RepoBadgePanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widgets');
    await settle(fixture);
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const badgeSrc = () => el().querySelector('img')!.getAttribute('src');
  const buttons = () => [...el().querySelectorAll('button')].map((b) => b.textContent!.trim());

  afterEach(() => vi.restoreAllMocks());

  it('fetches the repository and its branches', async () => {
    await create();
    expect(browse.getRepo).toHaveBeenCalledWith('github', 'acme', 'widgets');
    expect(browse.getBranches).toHaveBeenCalledWith('github', 'acme', 'widgets');
  });

  it('shows the default branch as text when it is the only one', async () => {
    await create();
    expect(el().querySelector('bs-select')).toBeNull();
    expect(el().textContent).toContain('Default branch (main):');
    // The default branch keeps the parameterless URL every existing README uses.
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg');
  });

  it('says "unknown" when the repository has no default branch', async () => {
    await create({ getRepo: () => Promise.resolve(repo({ defaultBranch: undefined })), getBranches: () => Promise.resolve([]) });
    expect(el().textContent).toContain('Default branch (unknown):');
  });

  it('offers a branch picker when more than one branch has coverage', async () => {
    await create({ getBranches: () => Promise.resolve(['main', 'feat/x y']) });

    const options = [...el().querySelectorAll('option')].map((o) => o.textContent!.trim());
    expect(options).toEqual(['main (default)', 'feat/x y']);
    expect(component.selectedBranch()).toBe('main');
  });

  it('points the badge at a picked branch, encoded', async () => {
    await create({ getBranches: () => Promise.resolve(['main', 'feat/x y']) });
    const select = fixture.debugElement.query((d) => d.componentInstance instanceof StubSelect).componentInstance as StubSelect;

    select.onChange('feat/x y');
    await settle(fixture);

    expect(component.selectedBranch()).toBe('feat/x y');
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg?branch=feat%2Fx%20y');

    // Picking the default again drops the parameter rather than spelling it out.
    component.selectBranch('main');
    await settle(fixture);
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg');
  });

  it('renders nothing when the repository cannot be loaded', async () => {
    await create({ getRepo: () => Promise.reject(new Error('404')) });
    expect(component.repo()).toBeNull();
    expect(el().querySelector('bs-card')).toBeNull();
  });

  // A branches failure costs the picker, not the badge.
  it('keeps the badge when the branches cannot be loaded', async () => {
    await create({ getBranches: () => Promise.reject(new Error('500')) });
    expect(component.branches()).toEqual([]);
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg');
  });

  it('hides the README section from non-managers', async () => {
    await create();
    expect(el().textContent).not.toContain('README badge');
    expect(buttons()).toEqual([]);
  });

  it('shows the README markdown built on the server base URL to managers', async () => {
    await create({ getRepo: () => Promise.resolve(repo({ canManage: true })) });

    expect(component.badgeMarkdown()).toBe(
      '[![Coverage](https://cov.example.test/badge/github/acme/widgets.svg)](https://cov.example.test/github/r/acme/widgets)');
    expect(el().querySelector('code')!.textContent).toBe(component.badgeMarkdown());
    // A public repository has no token to rotate.
    expect(buttons()).toEqual(['Copy markdown']);
  });

  it('falls back to location.origin without a configured base URL', async () => {
    await create({ getRepo: () => Promise.resolve(repo({ canManage: true, baseUrl: undefined })) });
    expect(component.badgeMarkdown()).toBe(
      `[![Coverage](${location.origin}/badge/github/acme/widgets.svg)](${location.origin}/github/r/acme/widgets)`);
  });

  it('offers to create a token for a private repository that has none', async () => {
    await create({ getRepo: () => Promise.resolve(repo({ canManage: true, isPrivate: true })) });

    expect(buttons()).toEqual(['Copy markdown', 'Create badge token']);
    expect(el().textContent).toContain('create a badge token to make the badge work');
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg');
  });

  it('puts a private repository token last in the badge URL', async () => {
    await create({
      getRepo: () => Promise.resolve(repo({ canManage: true, isPrivate: true, badgeToken: 'a+b/c' })),
      getBranches: () => Promise.resolve(['main', 'dev']),
    });
    component.selectBranch('dev');
    await settle(fixture);

    expect(buttons()).toContain('Rotate badge token');
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg?branch=dev&token=a%2Bb%2Fc');
    expect(el().textContent).not.toContain('create a badge token to make the badge work');
  });

  // A token on a public repository is inert; it must not leak into the copied URL.
  it('ignores a token on a public repository', async () => {
    await create({ getRepo: () => Promise.resolve(repo({ badgeToken: 'stale' })) });
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg');
  });

  it('copies the markdown to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    // jsdom has no clipboard; define one for this test and remove it again.
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    try {
      await create({ getRepo: () => Promise.resolve(repo({ canManage: true })) });
      (el().querySelector('button') as HTMLElement).click();
      await settle(fixture);
      expect(writeText).toHaveBeenCalledWith(component.badgeMarkdown());
    } finally {
      delete (navigator as { clipboard?: unknown }).clipboard;
    }
  });

  it('rotating stores the new token and rebuilds the URL', async () => {
    await create({
      getRepo: () => Promise.resolve(repo({ canManage: true, isPrivate: true, badgeToken: 'old' })),
      rotateBadgeToken: () => Promise.resolve({ badgeToken: 'new' }),
    });

    const rotate = [...el().querySelectorAll('button')].find((b) => b.textContent!.includes('Rotate')) as HTMLElement;
    rotate.click();
    await settle(fixture);

    expect(browse.rotateBadgeToken).toHaveBeenCalledWith('github', 'acme', 'widgets');
    expect(component.repo()!.badgeToken).toBe('new');
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg?token=new');
  });

  it('a failed rotation keeps the old token', async () => {
    await create({
      getRepo: () => Promise.resolve(repo({ canManage: true, isPrivate: true, badgeToken: 'old' })),
      rotateBadgeToken: () => Promise.reject(new Error('403')),
    });

    await expect(component.rotateBadgeToken()).rejects.toThrow('403');
    await settle(fixture);
    expect(component.repo()!.badgeToken).toBe('old');
    expect(badgeSrc()).toBe('/badge/github/acme/widgets.svg?token=old');
  });

  it('rotating before the repository loaded does nothing', async () => {
    await create({ getRepo: () => Promise.reject(new Error('404')) });
    await component.rotateBadgeToken();
    expect(browse.rotateBadgeToken).not.toHaveBeenCalled();
  });

  it('refetches when the repository input changes', async () => {
    await create();
    fixture.componentRef.setInput('name', 'gadgets');
    await settle(fixture);
    expect(browse.getRepo).toHaveBeenLastCalledWith('github', 'acme', 'gadgets');
    expect(browse.getBranches).toHaveBeenLastCalledWith('github', 'acme', 'gadgets');
  });
});
