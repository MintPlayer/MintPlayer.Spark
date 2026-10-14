import { provideZonelessChangeDetection, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { ShortShaRendererComponent } from './short-sha-renderer.component';
import { AccountLinkRendererComponent } from './account-link-renderer.component';
import { AccountAvatarRendererComponent, GROUP_AVATAR } from './account-avatar-renderer.component';
import { RepoNameRendererComponent } from './repo-name-renderer.component';
import { PrivateLockRendererComponent } from './private-lock-renderer.component';

/**
 * The renderers that read a SIBLING attribute off the row (Spark#245 item context). Each one
 * degrades silently when that read comes back empty — a link turns into text, a badge
 * disappears — so these pin the degraded form as deliberately as the happy path.
 */

function row(values: Record<string, unknown>, id = 'items/1'): QueryResultItem {
  return {
    id,
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
      item: row({ FullName: 'acme/widgets' }, 'Repositories/gitlab/1'),
    });

    const link = el(fixture).querySelector('a')!;
    expect(link.textContent!.trim()).toBe('abcdef1');
    expect(link.getAttribute('href')).toBe(`/gitlab/r/acme/widgets/c/${sha}`);
  });

  // No forge on the row means no link: a guessed forge would address a different account.
  it('renders plain text when the row id carries no forge', () => {
    const fixture = render(ShortShaRendererComponent, { value: sha, item: row({ FullName: 'acme/widgets' }) });

    expect(el(fixture).querySelector('a')).toBeNull();
    expect(el(fixture).querySelector('span')!.textContent!.trim()).toBe('abcdef1');
  });

  it('renders plain text when FullName is not owner/name', () => {
    const fixture = render(ShortShaRendererComponent, {
      value: sha,
      item: row({ FullName: 'widgets' }, 'Repositories/github/1'),
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
      const fixture = render(ShortShaRendererComponent, { value, item: row({ FullName: 'a/b' }, 'Repositories/github/1') });
      expect(el(fixture).querySelector('a, span')).toBeNull();
    }
  });

  it('takes the tooltip from the sibling attribute named by titleAttribute', () => {
    const fixture = render(ShortShaRendererComponent, {
      value: sha,
      options: { titleAttribute: 'Message' },
      item: row({ FullName: 'acme/widgets', Message: 'Fix the parser' }, 'Repositories/github/1'),
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
  // A MyAccountRow's id is its owner key, `{provider}:{login}`; the forge comes from there (#264).
  it('links the login to /{provider}/a/{login}, taking the forge from the row id', () => {
    const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item: row({}, 'github:mintplayer') });

    const link = el(fixture).querySelector('a')!;
    expect(link.textContent!.trim()).toBe('mintplayer');
    expect(link.getAttribute('href')).toBe('/github/a/mintplayer');
  });

  it('ignores a Provider value on the row: only the id names the forge', () => {
    const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item: row({ Provider: 'gitlab' }, 'github:mintplayer') });

    expect(el(fixture).querySelector('a')!.getAttribute('href')).toBe('/github/a/mintplayer');
  });

  // The pre-M7 route /a/{login} no longer exists; a login without a forge must not become a link.
  it('renders the login as plain text when the row id carries no provider', () => {
    for (const item of [undefined, row({}, 'mintplayer'), row({}, ':mintplayer'), row({}, 'GitHub:mintplayer'), { Provider: 'github' }]) {
      const fixture = render(AccountLinkRendererComponent, { value: 'mintplayer', item });
      expect(el(fixture).querySelector('a')).toBeNull();
      expect(el(fixture).textContent!.trim()).toBe('mintplayer');
    }
  });

  it('renders nothing for an empty or non-string login', () => {
    for (const value of [null, '', 42]) {
      const fixture = render(AccountLinkRendererComponent, { value, item: row({}, 'github:mintplayer') });
      expect(el(fixture).textContent!.trim()).toBe('');
      expect(fixture.componentInstance.link()).toBeNull();
    }
  });
});

describe('AccountAvatarRendererComponent', () => {
  it('draws the avatar image with the login as alt text', () => {
    const fixture = render(AccountAvatarRendererComponent, {
      value: 'https://avatars.example.test/u/1',
      item: row({ Login: 'mintplayer' }),
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

  // The server folds the account type into the cell (MyAccountRowActions.WithAvatarFallback).
  it('draws the group icon, and no image, for the server group marker', () => {
    const fixture = render(AccountAvatarRendererComponent, { value: GROUP_AVATAR, item: row({ Login: 'acme' }) });

    const icon = el(fixture).querySelector('i')!;
    expect(icon.classList.contains('bi-people')).toBe(true);
    expect(icon.classList.contains('bi-person')).toBe(false);
    expect(el(fixture).querySelector('img')).toBeNull();
  });

  it('falls back to the person icon when there is no avatar', () => {
    for (const value of [null, undefined, '']) {
      const fixture = render(AccountAvatarRendererComponent, { value, item: row({}) });
      const icon = el(fixture).querySelector('i')!;
      expect(icon.classList.contains('bi-person')).toBe(true);
      expect(icon.classList.contains('bi-people')).toBe(false);
    }
  });
});

describe('RepoNameRendererComponent', () => {
  it('draws just the name, whatever the row says about privacy', () => {
    for (const item of [row({ IsPrivate: true }), row({ IsPrivate: false }), undefined]) {
      const fixture = render(RepoNameRendererComponent, { value: 'widgets', item });
      expect(el(fixture).textContent!.trim()).toBe('widgets');
      expect(el(fixture).querySelector('bs-badge, i')).toBeNull();
    }
  });
});

describe('PrivateLockRendererComponent', () => {
  function po(): PersistentObject {
    return { id: 'Repositories/github/1', attributes: [] } as unknown as PersistentObject;
  }

  it('draws a lock in the grid for a private repository, and nothing else', () => {
    const fixture = render(PrivateLockRendererComponent, { value: true, item: row({ IsPrivate: true }) });

    expect(el(fixture).querySelector('i.bi-lock-fill')).not.toBeNull();
    expect(el(fixture).textContent!.trim()).toBe('');
  });

  it('draws nothing in the grid for a public repository or a non-boolean value', () => {
    for (const value of [false, 'true', null, undefined]) {
      const fixture = render(PrivateLockRendererComponent, { value, item: row({}) });
      expect(el(fixture).querySelector('i')).toBeNull();
      expect(el(fixture).textContent!.trim()).toBe('');
    }
  });

  it('says it in words on the detail page', () => {
    const privateRepo = render(PrivateLockRendererComponent, { value: true, item: po() });
    expect(el(privateRepo).querySelector('i.bi-lock-fill')).not.toBeNull();
    expect(el(privateRepo).textContent!.trim()).toBe('private');

    const publicRepo = render(PrivateLockRendererComponent, { value: false, item: po() });
    expect(el(publicRepo).querySelector('i')).toBeNull();
    expect(el(publicRepo).textContent!.trim()).toBe('public');
  });
});
