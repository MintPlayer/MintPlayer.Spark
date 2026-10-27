import type { RunResponse } from './engine';

export type ServerAnswer =
  | { kind: 'answer'; status: number; body: RunResponse }
  /** The app could not run it: the action falls back to the local engine (PRD D2). */
  | { kind: 'unavailable'; reason: string };

export interface ServerOptions {
  url: string;
  idToken: (audience: string) => Promise<string>;
  fetch?: typeof fetch;
  timeoutMs?: number;
}

/**
 * Asks the app to version the commit (PRD D5): the manifest as the body, the workflow's OIDC token (audience = the
 * app's URL) as the credential. 2xx and 4xx are the app's answer, a refusal the action fails on; a 5xx, a timeout, a
 * network error or no OIDC token is the app being unavailable. The engine is idempotent, so a run the server
 * half-finished is completed by the fallback rather than duplicated.
 */
export const runOnServer = (options: ServerOptions, manifest: unknown) => post(options, 'run', manifest);

/** #552 D6: the same run without its tags; the answer carries the plan and the app's seal over it. */
export const planOnServer = (options: ServerOptions, manifest: unknown) => post(options, 'plan', manifest);

/**
 * #552 D6: writes the sealed plan's tags. A 409 is a stale plan, which the action fails on rather than falling back:
 * the local engine would refuse it the same way (D3).
 */
export const releaseOnServer = (options: ServerOptions, seal: string) => post(options, 'release', { seal });

async function post(options: ServerOptions, route: 'run' | 'plan' | 'release', body: unknown): Promise<ServerAnswer> {
  const base = options.url.replace(/\/+$/, '');
  let token: string;
  try {
    token = await options.idToken(base);
  } catch (e) {
    return { kind: 'unavailable', reason: `no OIDC token (does the job have "permissions: id-token: write"?): ${(e as Error).message}` };
  }

  let response: Response;
  try {
    response = await (options.fetch ?? fetch)(`${base}/api/versioning/${route}`, {
      method: 'POST',
      headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json', accept: 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(options.timeoutMs ?? 10 * 60_000),
    });
  } catch (e) {
    return { kind: 'unavailable', reason: (e as Error).message };
  }

  if (response.status >= 500) return { kind: 'unavailable', reason: `the app answered ${response.status}: ${(await response.text()).slice(0, 500)}` };
  const text = await response.text();
  let parsed: RunResponse;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = { outcome: 'refused', reason: 'response', detail: text.slice(0, 500), releases: [], refused: [], skipped: 0, warnings: [] };
  }
  return { kind: 'answer', status: response.status, body: parsed };
}
