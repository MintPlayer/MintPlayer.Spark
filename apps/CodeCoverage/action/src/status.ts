import * as core from '@actions/core';
import { Credential } from './credential';

export interface CoverageSummary {
  linesCovered: number;
  linesCoverable: number;
  branchesCovered: number;
  branchesTotal: number;
  filesCount: number;
}

export interface UploadStatus {
  buildId: string;
  /** InFlight | Complete | CompleteWithErrors — the only field to branch on. */
  state: string;
  status: string;
  finalizeReason?: string | null;
  coverage?: CoverageSummary | null;
  baseline?: { sha: string; branch?: string | null; coverage?: CoverageSummary | null } | null;
  sessions?: { sessionId: string; jobName?: string | null; parseStatus: string; error?: string | null }[];
  commitUrl?: string | null;
  /** True when the build declared itself a subset (nx affected). */
  partial?: boolean;
  /** Which base the comparison actually used, and how far it strayed. */
  baselineScope?: {
    mode: string;
    requestedBaseSha?: string | null;
    resolvedBaseSha?: string | null;
    baseResolution: string;
    filesInScope?: number | null;
    prunedFiles?: number | null;
  } | null;
  /** Patched whole-workspace projection with its completeness verdict. */
  projection?: { coverage: CoverageSummary; complete: boolean; incompleteReasons: string[] } | null;
  /** Added-lines coverage vs the diff base. */
  patch?: {
    diffBaseRef?: string | null;
    mergeBaseSha?: string | null;
    linesCovered: number;
    linesCoverable: number;
    filesInDiff: number;
    filesMatched: number;
    diffTruncated: boolean;
  } | null;
}

export interface WaitOptions {
  url: string;
  repository: string;
  commitSha: string;
  runId: number;
  runAttempt: number;
  timeoutSeconds: number;
  pollIntervalSeconds: number;
}

export class WaitTimeout extends Error {}

const MAX_INTERVAL_SECONDS = 60;
/** Poll briskly while a build is likely to finish soon, then ease off. */
const BACKOFF_AFTER_SECONDS = 60;
const RELAXED_INTERVAL_SECONDS = 15;

/**
 * Polls `/api/uploads/status` until the build reaches a terminal state, and
 * returns it. Throws {@link WaitTimeout} if the deadline passes first.
 *
 * Exits on **any** state other than `InFlight`, not on `Complete` specifically —
 * a build that finishes with a failed session is finished, and waiting for it to
 * become clean would burn the whole timeout. Judging whether
 * `CompleteWithErrors` is acceptable belongs to the caller.
 */
export async function waitForFinalize(credential: Credential, options: WaitOptions): Promise<UploadStatus> {
  const deadline = Date.now() + options.timeoutSeconds * 1000;
  const query = new URLSearchParams({
    repository: options.repository,
    commitSha: options.commitSha,
    runId: String(options.runId),
    runAttempt: String(options.runAttempt),
  });
  const endpoint = `${options.url}/api/uploads/status?${query}`;

  let interval = options.pollIntervalSeconds;
  let refreshed = false;
  const startedAt = Date.now();

  for (;;) {
    const response = await fetch(endpoint, {
      headers: { Authorization: `Bearer ${await credential.get()}` },
    });

    if (response.ok) {
      const status = (await response.json()) as UploadStatus;
      if (status.state !== 'InFlight') return status;
      refreshed = false;
      interval =
        Date.now() - startedAt > BACKOFF_AFTER_SECONDS * 1000
          ? Math.max(interval, RELAXED_INTERVAL_SECONDS)
          : options.pollIntervalSeconds;
    } else if (response.status === 429) {
      // Rate limited is "wait longer", never "give up": only the deadline ends
      // a wait. Several jobs of one workflow waiting at once share a bucket.
      interval = retryAfter(response) ?? Math.min(interval * 2, MAX_INTERVAL_SECONDS);
      core.info(`Coverage server is rate limiting; waiting ${interval}s.`);
    } else if (response.status === 401 && !refreshed) {
      // An OIDC token expired mid-wait. Re-mint once before believing it.
      core.debug('Status poll returned 401 — refreshing the credential and retrying.');
      credential.invalidate();
      refreshed = true;
      continue;
    } else {
      throw new Error(`${endpoint} responded ${response.status}: ${await safeText(response)}`);
    }

    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      throw new WaitTimeout(
        `Timed out after ${options.timeoutSeconds}s waiting for the build to finalize. ` +
          `It may still finish — the server finalizes every build within 30 minutes.`,
      );
    }
    await new Promise((resolve) => setTimeout(resolve, Math.min(interval * 1000, remaining)));
  }
}

/** Percentage to one decimal. `0/0` is not 100% — it is no data. */
export function rate(covered?: number, coverable?: number): string {
  if (!coverable || coverable <= 0 || covered === undefined) return '';
  return (Math.round((covered / coverable) * 1000) / 10).toFixed(1);
}

function retryAfter(response: Response): number | undefined {
  const header = response.headers.get('retry-after');
  if (!header) return undefined;
  const seconds = parseInt(header, 10);
  return Number.isFinite(seconds) && seconds > 0 ? Math.min(seconds, MAX_INTERVAL_SECONDS) : undefined;
}

async function safeText(response: Response): Promise<string> {
  try {
    return (await response.text()).slice(0, 500);
  } catch {
    return '';
  }
}
