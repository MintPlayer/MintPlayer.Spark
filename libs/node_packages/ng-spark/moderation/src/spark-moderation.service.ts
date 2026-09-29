import { computed, inject, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { EMPTY, filter } from 'rxjs';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  ModerationAuditEntry,
  ModerationCaseDetail,
  ModerationCaseSummary,
  ModerationReputation,
  ModerationReputationLine,
  ModerationStatus,
  ModerationVoteState,
} from './spark-moderation.models';

/**
 * Client for `MintPlayer.Spark.Moderation`'s endpoints (`/spark/moderation/*`, #460). Every call is
 * a POST with the ids in the body — the route table is literal and RavenDB ids contain slashes.
 *
 * A target the caller cannot see (hidden, missing, not moderatable) is the standard refusal: 404
 * (or 401 when signing in would help). A rule (own post, locked, suspended) is a 400 with `errors`,
 * a quota (votes per day) a 429 whose `result.retryAfterSeconds` says when to try again.
 */
@Injectable({ providedIn: 'root' })
export class SparkModerationService {
  private readonly spark = inject(SparkService);

  // Vote states are fetched in batches: every widget that asks within one macrotask shares a request.
  private readonly pending = new Map<string, Map<string, ((state: ModerationVoteState | null) => void)[]>>();
  private flushScheduled = false;

  private readonly router = inject(Router, { optional: true });

  readonly #votesCast = signal(0);

  /** Counts the caller's accepted votes, so the own reputation badge re-reads (a downvote costs reputation). */
  readonly votesCast = this.#votesCast.asReadonly();

  private readonly navigated = toSignal(
    this.router?.events.pipe(filter(e => e instanceof NavigationEnd)) ?? EMPTY,
    { initialValue: null });

  /**
   * Changes whenever the caller's own reputation may have changed: after every navigation (a
   * sign-in, a sign-out, another page) and after the caller's own votes. The own badge and the
   * review-queue link read it to re-fetch {@link ownReputation}; a component in the app shell lives
   * for the whole session and would otherwise show the first answer until a full reload.
   */
  // A fresh object each time: returning the vote count alone would compare equal after a navigation
  // and notify nobody.
  readonly ownReputationChanged = computed(() => ({ navigation: this.navigated(), votes: this.#votesCast() }));

  #ownInFlight: Promise<ModerationReputation> | null = null;

  /**
   * The caller's own reputation, shared by every caller in flight at once: the badge and the
   * review-queue link both re-read on the same navigation and cost one request.
   */
  ownReputation(): Promise<ModerationReputation> {
    return this.#ownInFlight ??= this.reputation().finally(() => this.#ownInFlight = null);
  }

  /** Casts (+1 / −1) or withdraws (0) the caller's vote; resolves to the target's new state. */
  async vote(type: string, id: string, direction: -1 | 0 | 1): Promise<ModerationVoteState> {
    const state = await this.spark.postEnvelope<ModerationVoteState>('/moderation/vote', { objectTypeId: type, id, direction });
    this.#votesCast.update(n => n + 1);
    return state;
  }

  /**
   * The score and the caller's vote for one target, batched with every other call made in the same
   * macrotask for the same type (one `POST /spark/moderation/votes`, ≤ 100 ids). Resolves to `null`
   * for an id the server left out (not visible to the caller).
   */
  voteState(type: string, id: string): Promise<ModerationVoteState | null> {
    return new Promise(resolve => {
      let byId = this.pending.get(type);
      if (!byId) this.pending.set(type, (byId = new Map()));
      const waiting = byId.get(id) ?? [];
      waiting.push(resolve);
      byId.set(id, waiting);
      if (!this.flushScheduled) {
        this.flushScheduled = true;
        setTimeout(() => this.flush(), 0);
      }
    });
  }

  /** `POST /spark/moderation/votes` for up to 100 ids of one type. */
  votes(type: string, ids: string[]): Promise<ModerationVoteState[]> {
    return this.spark.postEnvelope<ModerationVoteState[]>('/moderation/votes', { objectTypeId: type, ids });
  }

  flag(type: string, id: string, reason: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/flag', { objectTypeId: type, id, reason });
  }

  lock(type: string, id: string, reason?: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/lock', { objectTypeId: type, id, reason });
  }

  unlock(type: string, id: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/unlock', { objectTypeId: type, id });
  }

  /** What the moderator panel shows about a target, including which tools the caller holds. */
  status(type: string, id: string): Promise<ModerationStatus> {
    return this.spark.postEnvelope<ModerationStatus>('/moderation/status', { objectTypeId: type, id });
  }

  /** A user's reputation; the caller's own when `userId` is omitted (with privileges and suspension). */
  reputation(userId?: string): Promise<ModerationReputation> {
    return this.spark.postEnvelope<ModerationReputation>('/moderation/reputation', userId ? { userId } : {});
  }

  /** The caller's own ledger, newest first. Voters are never named; a reversal reads "Voting corrected (−N)". */
  history(take = 100): Promise<ModerationReputationLine[]> {
    return this.spark.postEnvelope<ModerationReputationLine[]>('/moderation/reputation/history', { take });
  }

  cases(status: string = 'open', skip = 0, take = 50): Promise<ModerationCaseSummary[]> {
    return this.spark.postEnvelope<ModerationCaseSummary[]>('/moderation/cases', { status, skip, take });
  }

  case(caseId: string): Promise<ModerationCaseDetail> {
    return this.spark.postEnvelope<ModerationCaseDetail>('/moderation/case', { caseId });
  }

  /** `uphold` / `decline` (flag cases), `dismiss` / `reverse` / `merge` / `suspend` (fraud cases; the last two name `accountId`). */
  decide(caseId: string, decision: string, accountId?: string, reason?: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/case/decide', { caseId, decision, accountId, reason });
  }

  suspend(userId: string, days?: number | null, reason?: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/suspend', { userId, days: days ?? null, reason });
  }

  unsuspend(userId: string): Promise<void> {
    return this.spark.postEnvelope<void>('/moderation/unsuspend', { userId });
  }

  audit(skip = 0, take = 50): Promise<ModerationAuditEntry[]> {
    return this.spark.postEnvelope<ModerationAuditEntry[]>('/moderation/audit', { skip, take });
  }

  private flush(): void {
    this.flushScheduled = false;
    const batches = [...this.pending.entries()];
    this.pending.clear();
    for (const [type, byId] of batches) {
      const ids = [...byId.keys()];
      for (let i = 0; i < ids.length; i += 100) {
        const chunk = ids.slice(i, i + 100);
        this.votes(type, chunk).then(
          states => {
            const found = new Map(states.map(s => [s.id, s] as const));
            for (const id of chunk) byId.get(id)?.forEach(resolve => resolve(found.get(id) ?? null));
          },
          () => {
            for (const id of chunk) byId.get(id)?.forEach(resolve => resolve(null));
          });
      }
    }
  }
}
