import type { Location } from '@angular/common';
import { describe, expect, it } from 'vitest';
import { SparkAppBase, SparkAppUrl, sparkAppUrl, sparkInAppUrl } from './spark-app-url';

/**
 * The one classifier for a link into or out of the application (owner, 2026-10-15): the program-unit menu and
 * notification clicks both ask it. Every row is a case where a looser rule sends a visitor somewhere else.
 */
const atRoot: SparkAppBase = { origin: 'https://app.test', baseHref: '/' };
const underApp: SparkAppBase = { origin: 'https://app.test', baseHref: '/app/' };

const internal = (route: string): SparkAppUrl => ({ kind: 'internal', route });
const external = (href: string): SparkAppUrl => ({ kind: 'external', href });

describe('sparkAppUrl', () => {
  it.each<[string, SparkAppBase, SparkAppUrl]>([
    ['/merge-queue/setup', atRoot, internal('/merge-queue/setup')],
    ['/merge-queue/setup?provider=github&account=acme#permissions', atRoot, internal('/merge-queue/setup?provider=github&account=acme#permissions')],
    ['/', atRoot, internal('/')],
    // A trailing slash is not part of the route (Location.normalize strips it too).
    ['/merge-queue/setup/', atRoot, internal('/merge-queue/setup')],
    ['https://app.test/merge-queue/setup/', atRoot, internal('/merge-queue/setup')],
    // Scheme and host are not case-sensitive, and 443 is https's own port: the same origin.
    ['HTTPS://APP.TEST:443/merge-queue/setup', atRoot, internal('/merge-queue/setup')],
    ['//app.test/merge-queue/setup', atRoot, internal('/merge-queue/setup')],
    // Another port, or http from an https page, is another origin.
    ['https://app.test:8443/merge-queue/setup', atRoot, external('https://app.test:8443/merge-queue/setup')],
    ['http://app.test/merge-queue/setup', atRoot, external('http://app.test/merge-queue/setup')],
    ['https://other.test/x', atRoot, external('https://other.test/x')],
    // What the browser reads as another host is another host, whatever it looks like.
    ['//evil.test/x', atRoot, external('https://evil.test/x')],
    ['/\\evil.test/x', atRoot, external('https://evil.test/x')],
    ['/\t/evil.test/x', atRoot, external('https://evil.test/x')],
    ['/\n/evil.test/x', atRoot, external('https://evil.test/x')],
    [' https://evil.test/x', atRoot, external('https://evil.test/x')],
    // A leading space is trimmed, as the browser trims it.
    [' /merge-queue/setup', atRoot, internal('/merge-queue/setup')],
    // A colon in an app path's first segment is not a scheme.
    ['/a:b', atRoot, internal('/a:b')],
    // Under /app/, an app path resolves below the base, like routerLink.
    ['/merge-queue/setup', underApp, internal('/merge-queue/setup')],
    ['https://app.test/app/merge-queue/setup', underApp, internal('/merge-queue/setup')],
    // The base itself, with or without its slash.
    ['https://app.test/app', underApp, internal('/')],
    ['https://app.test/app/', underApp, internal('/')],
    ['/', underApp, internal('/')],
    // Compared per segment and case-sensitively, and never above the base.
    ['https://app.test/application/x', underApp, external('https://app.test/application/x')],
    ['https://app.test/APP/x', underApp, external('https://app.test/APP/x')],
    ['https://app.test/other/x', underApp, external('https://app.test/other/x')],
    ['/../other/x', underApp, external('https://app.test/other/x')],
    ['/%2e%2e/other/x', underApp, external('https://app.test/other/x')],
    // An absolute base href counts by its path.
    ['https://app.test/app/x', { origin: 'https://app.test', baseHref: 'https://app.test/app/' }, internal('/x')],
  ])('%j under %j', (url, base, expected) => {
    expect(sparkAppUrl(url, base)).toEqual(expected);
  });

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    'java\tscript:alert(1)',
    ' javascript:alert(1)',
    'data:text/html,<b>x</b>',
    'vbscript:x',
    'file:///etc/passwd',
    'mailto:someone@example.com',
    'http://[',
    '',
    ' \t ',
  ])('is null for %j: no scheme but http(s), nothing unparseable', url => {
    expect(sparkAppUrl(url, atRoot)).toBeNull();
    expect(sparkAppUrl(url, underApp)).toBeNull();
  });

  it('is null for a missing url', () => {
    expect(sparkAppUrl(null, atRoot)).toBeNull();
    expect(sparkAppUrl(undefined, atRoot)).toBeNull();
  });
});

describe('sparkInAppUrl', () => {
  // A Location over the base href /app/: prepareExternalUrl('/') is the base.
  const location = { prepareExternalUrl: (url: string) => '/app' + url } as unknown as Location;

  it('is the router URL below the base href for a link into the application', () => {
    expect(sparkInAppUrl(`${document.location.origin}/app/runs/42?tab=issues#top`, document, location)).toBe('/runs/42?tab=issues#top');
    expect(sparkInAppUrl('/runs/42', document, location)).toBe('/runs/42');
  });

  it('is null for a same-origin link outside the base href', () => {
    expect(sparkInAppUrl(`${document.location.origin}/other/x`, document, location)).toBeNull();
    expect(sparkInAppUrl(`${document.location.origin}/application/x`, document, location)).toBeNull();
  });
});
