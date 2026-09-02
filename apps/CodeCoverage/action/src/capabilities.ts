import * as core from '@actions/core';
import { Credential } from './credential';

/**
 * What the server on the other end can actually do.
 *
 * The action is consumed from a git ref; the server ships as a docker image a VPS
 * pulls. Those two clocks are independent — a merged action is live for every
 * consumer immediately, while the server it talks to is whatever was last
 * deployed. So "the action and the server are in the same commit" is never a
 * compatibility guarantee, and this probe is how the action finds out what it is
 * really talking to.
 */
export interface ServerCapabilities {
  /**
   * The upload contract the server implements. Incremented only for a change the
   * action cannot absorb silently; additive fields never move it.
   */
  contract: number;
  features: string[];
}

/**
 * A server that has never heard of this endpoint. `contract: 0` is not an error
 * state — it is exactly what every image deployed before the endpoint existed
 * reports, which is what makes an old image self-describing without being
 * touched.
 */
export const BASELINE: ServerCapabilities = { contract: 0, features: [] };

/** The highest contract this action knows how to speak. */
export const CLIENT_CONTRACT = 1;

/**
 * Probes the server, degrading to {@link BASELINE} for every failure.
 *
 * This never throws and never fails the step. A capability probe that could
 * break an upload would be strictly worse than not probing: the upload itself
 * reports auth and connectivity problems with far better messages, and it runs
 * either way.
 */
export async function fetchCapabilities(url: string, credential: Credential): Promise<ServerCapabilities> {
  let response: Response;
  try {
    response = await fetch(`${url}/api/uploads/capabilities`, {
      headers: { Authorization: `Bearer ${await credential.get()}` },
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    core.debug(`Capability probe failed (${message}); assuming the baseline contract.`);
    return BASELINE;
  }

  // The deployed image predates the endpoint. Expected, not noteworthy.
  if (response.status === 404) {
    core.debug('Server does not advertise capabilities; assuming the baseline contract.');
    return BASELINE;
  }

  if (!response.ok) {
    // 401 included: the upload below reports an auth failure properly, and
    // duplicating it here would just add a confusing second message.
    core.debug(`Capability probe responded ${response.status}; assuming the baseline contract.`);
    return BASELINE;
  }

  try {
    const body = (await response.json()) as Partial<ServerCapabilities>;
    return {
      contract: typeof body.contract === 'number' ? body.contract : 0,
      features: Array.isArray(body.features) ? body.features.filter((f) => typeof f === 'string') : [],
    };
  } catch {
    core.debug('Capability response was not readable JSON; assuming the baseline contract.');
    return BASELINE;
  }
}

/**
 * Warns about each input that this server will silently ignore.
 *
 * Silence is the hazard being addressed. ASP.NET model binding drops unknown
 * multipart fields, so `partial: true` sent to a server that predates partial
 * uploads is not rejected — it is accepted, and the build is then compared
 * against a whole-workspace baseline as though it had measured everything. The
 * number that comes back is wrong in the direction that looks fine.
 */
export function warnAboutUnsupportedInputs(
  capabilities: ServerCapabilities,
  requested: { partial: boolean; carryForward?: boolean },
): void {
  if (capabilities.contract === 0) {
    // Nothing is known about this server, so nothing specific can be claimed.
    // Saying so once beats a warning per input.
    core.info('Server does not advertise an upload contract (pre-capabilities image); uploading anyway.');
    return;
  }

  if (requested.partial && !capabilities.features.includes('partial-uploads')) {
    core.warning(
      'This server does not support partial uploads, so `partial: true` and `base-sha:` will be ignored and ' +
        'the result will be compared against a whole-workspace baseline. Treat the reported number with care.',
    );
  }

  if (
    requested.partial &&
    capabilities.features.includes('partial-uploads') &&
    !capabilities.features.includes('carry-forward')
  ) {
    core.warning(
      'This server does not carry coverage forward for partial uploads: files `nx affected` skipped will be missing from ' +
        'the commit total rather than filled in from the base. Upgrade the server to get whole-workspace numbers from affected runs.',
    );
  }

  if (CLIENT_CONTRACT > capabilities.contract) {
    core.info(
      `This action speaks upload contract ${CLIENT_CONTRACT}; the server implements ${capabilities.contract}. ` +
        'Newer inputs degrade rather than fail.',
    );
  }
}
