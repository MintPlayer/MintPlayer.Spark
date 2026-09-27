import { provideZonelessChangeDetection, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { QueryResultItem } from '@mintplayer/ng-spark/models';
import { ShortShaRendererComponent } from './short-sha-renderer.component';
import { AccountLinkRendererComponent } from './account-link-renderer.component';
import { AccountAvatarRendererComponent } from './account-avatar-renderer.component';
import { RepoNameRendererComponent } from './repo-name-renderer.component';

/**
 * The renderers that read a SIBLING attribute off the row (Spark#245 item context). Each one
 * degrades silently when that read comes back empty — a link turns into text, a badge
 * disappears — so these pin the degraded form as deliberately as the happy path.
 */

function row(values: Record<string, unknown>): QueryResultItem {
  return {
    id: 'items/1',
    values: Object.entries(values).map(([key, value]) => ({ key, value })),
  } as unknown as QueryResultItem;
}

function render<T>(type: Type<T>, inputs: Record<string, unknown>): ComponentFixture<T> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  return fixture;
}

function el(fixture: ComponentFixture<unknown>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

beforeEach(async () => {
  await TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([])],
  }).compileComponents();
});

describe('ShortShaRendererComponent', () => {
  const sha = 'abcdef1234567890';

  it('links the 7-char sha to the forge-scoped vanity commit URL, with the full sha in the route', () => {
    const fixture = render(ShortShaRendererComponent, {
      value: sha,
      item: row({ FullName: 'acme/widgets', OwnerKey: 'gitlab:acme' }),
    });

    const link = el(fixture).querySelector('a')!;
    expect(link.textContent!.trim()).toBe('abcdef1');
    expect(link.getAttribute('href')).toBe(`/gitlab/r/acme/widgets/c/${sha}`);
  });

  // No forge on the row means no link: a guessed forge would address a different account.
  it('renders plain text when the row carries no OwnerKey', () => {
    const fixture = render(ShortShaRendererComponent, { value: sha, item: row({ FullName: 'acme/widgets' }) });

    expect(el(fixture).querySelector('a')).toBeNull();
    expect(el(fixture).querySelector('span')!.textContent!.trim()).toBe('abcdef1');
  });

  it('renders plain text when FullName is not owner/name', () => {
    const fixture = render(ShortShaRendererComponent, {
      value: sha,
      item: row({ FullName: 'widgets', OwnerKey: 'github:acme' }),
    });

    expect(el(fixture).querySelector('a')).toBeNull();
    expect(el(fixture).querySelector('span')).not.toBeNull();
  });

  it('renders plain text without any row context', () => {
    const fixture = render(ShortShaRendererComponent, { value: sha });

    expect(el(fixture).querySelector('a')).toBeNull();
    expect(el(fixture).textContent!.trim()).toBe('abcdef1');
  });

  it('renders nothing for an empty or non-string value', () => {
    for (const value of [null, undefined, '', 1234567, { sha }]) {
      const fixture = render(ShortShaRendererComponent, { value, item: row({ FullName: 'a/b', OwnerKey: 'github:a' }) });
      expect(el(fixture).querySelector('a, span')).toBeNull();
    }
  });

  it('takes the tooltip from the sibling attribute named by titleAttribute', () => {
    const fixture = render(ShortShaRendererComponent, {
      value: sha,
      options: { titleAttribute: 'Message' },
      item: row({ FullName: 'acme/widgets', OwnerKey: 'github:acme', Message: 'Fix the parser' }),
    });

    expect(el(fixture).querySelector('a')!.getAttribute('title')).toBe('Fix the parser');
  });

  it('leaves the tooltip empty when titleAttribute is absent, not a string, or names an empty value', () => {
    const cases: Record<string, unknown>[] = [
      {},
      { titleAttribute: 42 },
      { titleAttribute: 'Message' },
    ];
    for (const options of cases) {
      const fixture = render(ShortShaRendererComponent, { value: sha, options, item: row({ Message: '' }) });
      expect(el(fixture).querySelector('span')!.getAttribute('title')).toBe('');
      expect(fixture.componentInstance.tooltip()).toBeNull();
    }
  });
});

describe('AccountLinkRendererComponent', () => {
  it('links the login to /{provider}/a/{login}, taking the forge from the row', () => {
    const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item: row({ Provider: 'github' }) });

    const link = el(fixture).querySelector('a')!;
    expect(link.textContent!.trim()).toBe('mintplayer');
    expect(link.getAttribute('href')).toBe('/github/a/mintplayer');
  });

  it('reads the provider off a flat record row too', () => {
    const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item: { Provider: 'gitlab' } });

    expect(el(fixture).querySelector('a')!.getAttribute('href')).toBe('/gitlab/a/mintplayer');
  });

  // The pre-M7 route /a/{login} no longer exists; a login without a forge must not become a link.
  it('renders the login as plain text when the row has no provider', () => {
    for (const item of [undefined, row({}), row({ Provider: '' }), row({ Provider: 7 })]) {
      const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item });
      expect(el(fixture).querySelector('a')).toBeNull();
      expect(el(fixture).textContent!.trim()).toBe('mintplayer');
    }
  });

  it('renders nothing for an empty or non-string login', () => {
    for (const value of [null, '', 42]) {
      const fixture = render(AccountLinkRendererComponent, { value, item: row({ Provider: 'github' }) });
      expect(el(fixture).textContent!.trim()).toBe('');
      expect(fixture.componentInstance.link()).toBeNull();
    }
  });
});

describe('AccountAvatarRendererComponent', () => {
  it('draws the avatar image with the login as alt text', () => {
    const fixture = render(AccountAvatarRendererComponent, {
      value: 'https://avatars.example.test/u/1',
      item: row({ Login: 'mintplayer', Type: 'User' }),
    });

    const img = el(fixture).querySelector('img')!;
    expect(img.getAttribute('src')).toBe('https://avatars.example.test/u/1');
    expect(img.getAttribute('alt')).toBe('mintplayer');
    expect(el(fixture).querySelector('i')).toBeNull();
  });

  it('uses an empty alt when the row has no string Login', () => {
    const fixture = render(AccountAvatarRendererComponent, { value: 'https://avatars.example.test/u/1', item: row({ Login: 5 }) });

    expect(el(fixture).querySelector('img')!.getAttribute('alt')).toBe('');
  });

  it('falls back to the group icon for every forge spelling of an organisation, case-insensitively', () => {
    for (const type of ['Organization', 'organisation', 'group', 'Team', 'WORKSPACE']) {
      const fixture = render(AccountAvatarRendererComponent, { value: '', item: row({ Type: type }) });
      const icon = el(fixture).querySelector('i')!;
      expect(icon.classList.contains('bi-people')).toBe(true);
      expect(icon.classList.contains('bi-person')).toBe(false);
    }
  });

  // Deny-list on purpose: a value we do not recognise is a person, never "nothing".
  it('falls back to the person icon for users, unknown types and a missing type', () => {
    for (const item of [row({ Type: 'User' }), row({ Type: 'Bot' }), row({}), undefined]) {
      const fixture = render(AccountAvatarRendererComponent, { value: null, item });
      const icon = el(fixture).querySelector('i')!;
      expect(icon.classList.contains('bi-person')).toBe(true);
      expect(icon.classList.contains('bi-people')).toBe(false);
    }
  });
});

describe('RepoNameRendererComponent', () => {
  it('shows the private badge only when the row says IsPrivate === true', () => {
    const fixture = render(RepoNameRendererComponent, { value: 'widgets', item: row({ IsPrivate: true }) });

    expect(el(fixture).textContent).toContain('widgets');
    expect(el(fixture).querySelector('bs-badge')!.textContent!.trim()).toBe('private');
  });

  it('draws no badge for a public repository, a truthy non-boolean, or a row without IsPrivate', () => {
    for (const item of [row({ IsPrivate: false }), row({ IsPrivate: 'true' }), row({}), undefined]) {
      const fixture = render(RepoNameRendererComponent, { value: 'widgets', item });
      expect(el(fixture).textContent).toContain('widgets');
      expect(el(fixture).querySelector('bs-badge')).toBeNull();
    }
  });
});
