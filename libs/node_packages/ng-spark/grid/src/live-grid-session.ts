import { BehaviorSubject, Subscription } from 'rxjs';
import { LiveErrorEvent, LiveQueryRequest, QueryResult } from '@mintplayer/ng-spark/models';
import { SparkLiveService, canonicalJson } from '@mintplayer/ng-spark/services';

/**
 * How long a page change waits for the server's answer before it shows the page it holds.
 *
 * The server diffs every evaluation against the page the client holds and sends NOTHING when nothing
 * visible changed (guide-live-updates.md, "nothing is sent when nothing visible changed") — and an `update`
 * is answered the same way. A page change always moves `skip`, so it is always answered; a new sort, search
 * or filter that leaves the page identical is not, and the page held is then already the right one. This
 * bound is how long "not answered yet" is told apart from "identical".
 */
export const LIVE_GRID_SETTLE_MS = 1000;

/**
 * One grid's live subscription (X4): the grid's current `/execute` body as ONE server subscription, sent as
 * an `update` whenever the datatable asks for another page, sort, search or filter.
 *
 * It sits behind the datatable's `[fetch]`, so server-side paging is untouched: a fetch for the request
 * already held resolves at once with the latest pushed page; a fetch for a new request sends it and waits
 * for the answer. A push nobody waited for calls `onPush`, and the grid asks the datatable to re-fetch, which
 * lands here and is served from memory.
 *
 * An `error` ends it for good (`failed`): every later fetch resolves empty, and nothing is retried.
 */
export class LiveGridSession {
  private readonly requests = new BehaviorSubject<LiveQueryRequest | null>(null);
  private readonly subscription: Subscription;
  private currentKey: string | null = null;
  private latest: QueryResult | null = null;
  private waiters: ((result: QueryResult | null) => void)[] = [];
  failed: LiveErrorEvent | null = null;

  constructor(
    live: SparkLiveService,
    private readonly onPush: () => void,
    private readonly onError: (error: LiveErrorEvent) => void,
    private readonly settleMs = LIVE_GRID_SETTLE_MS,
  ) {
    this.subscription = live.watchQuery(this.requests).subscribe(event => {
      if (event.type === 'result') {
        this.latest = event.result;
        if (this.waiters.length) this.resolveWaiters(event.result);
        else this.onPush();
      } else {
        this.failed = event;
        this.latest = null;
        this.resolveWaiters(null);
        this.onError(event);
      }
    });
  }

  /** The page for `request`: the held one when it is the request in force, else the server's answer. */
  fetch(request: LiveQueryRequest): Promise<QueryResult | null> {
    if (this.failed) return Promise.resolve(null);
    const key = canonicalJson(request);
    if (key === this.currentKey) {
      if (this.waiters.length === 0 && this.latest) return Promise.resolve(this.latest);
      return new Promise(resolve => this.waiters.push(resolve));
    }

    // The first request is a `subscribe`, always answered (`result` or `error`); later ones are `update`s.
    const answered = this.currentKey === null || this.latest === null;
    this.currentKey = key;
    this.requests.next(request);
    return new Promise(resolve => {
      let timer: ReturnType<typeof setTimeout> | null = null;
      const waiter = (result: QueryResult | null) => {
        if (timer) clearTimeout(timer);
        resolve(result);
      };
      this.waiters.push(waiter);
      if (!answered) {
        // Unanswered: the page held is the page (see LIVE_GRID_SETTLE_MS), for every fetch waiting on it.
        timer = setTimeout(() => this.resolveWaiters(this.latest), this.settleMs);
      }
    });
  }

  dispose(): void {
    this.subscription.unsubscribe();
    this.requests.complete();
    this.resolveWaiters(null);
  }

  private resolveWaiters(result: QueryResult | null): void {
    const waiters = this.waiters;
    this.waiters = [];
    for (const waiter of waiters) waiter(result);
  }
}
