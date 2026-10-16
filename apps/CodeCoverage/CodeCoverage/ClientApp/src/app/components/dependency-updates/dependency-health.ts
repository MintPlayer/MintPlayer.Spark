import type { DependencyHealth } from '../../services/browse.service';

/** Each health word as a reader would say it, with the theme colour of its chip (dependency-updates PRD D-N5). */
export const HEALTH: Record<DependencyHealth, { label: string; color: string }> = {
  off: { label: 'off', color: 'secondary' },
  new: { label: 'not run yet', color: 'secondary' },
  onboarding: { label: 'onboarding pull request open', color: 'info' },
  blocked: { label: 'onboarding blocked', color: 'warning' },
  running: { label: 'running', color: 'info' },
  waiting: { label: 'waiting for CI', color: 'primary' },
  bisecting: { label: 'bisecting', color: 'warning' },
  red: { label: 'CI red', color: 'danger' },
  frozen: { label: 'frozen (needs a person)', color: 'warning' },
  silent: { label: 'no answer from a run', color: 'warning' },
  failed: { label: 'failed', color: 'danger' },
  ok: { label: 'ok', color: 'success' },
};

/** What each readiness status means, in a few words, with its chip colour. */
export const READINESS: Record<string, { label: string; color: string }> = {
  'on-target': { label: 'on it', color: 'success' },
  'in-pull-request': { label: 'in the pull request', color: 'success' },
  ready: { label: 'ready', color: 'success' },
  blocked: { label: 'blocked', color: 'danger' },
  held: { label: 'held', color: 'warning' },
  'not-offered': { label: 'not offered yet', color: 'secondary' },
};

/**
 * A chip's classes: Bootstrap's subtle background with its emphasis text and border, a pair made for both themes (as the
 * merge queue's states, merge-queue PRD §0 D75, D84). Not a bare `.badge`: ng-bootstrap styles `.badge` only inside
 * `<bs-badge>`, so `.du-state` carries the shape itself.
 */
export function chip(color: string): string {
  return `du-state bg-${color}-subtle text-${color}-emphasis border border-${color}-subtle`;
}

const DATE_TIME = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

/** The server's times are UTC; one without a zone designator is read as UTC rather than as local time. */
export function time(utc: string | null | undefined): string {
  if (!utc) return '—';
  const date = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(utc) ? utc : `${utc}Z`);
  return Number.isNaN(date.getTime()) ? utc : DATE_TIME.format(date);
}

/** The vanity URL of a repository's page, from its full name. */
export function repoLink(provider: string, fullName: string): string[] {
  const [owner, name] = fullName.split('/');
  return ['/', provider, 'r', owner, name];
}
