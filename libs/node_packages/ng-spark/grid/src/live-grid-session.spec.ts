import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { Observable, Subject } from 'rxjs';

import { LiveGridSession } from './live-grid-session';

/** The session behind a live grid's `[fetch]`, against a stubbed SparkLiveService. */
describe('LiveGridSession', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const page = (...ids: string[]) => ({ columns: [], items: ids.map(id => ({ id, values: [] })), totalItems: ids.length, skip: 0, take: 10 } as any);

  function make() {
    const events = new Subject<any>();
    const requests: any[] = [];
    const live: any = {
      watchQuery: (requests$: Observable<any>) => new Observable<any>(subscriber => {
        const a = requests$.subscribe(r => { if (r) requests.push(r); });
        const b = events.subscribe(subscriber);
        return () => { a.unsubscribe(); b.unsubscribe(); };
      }),
    };
    const onPush = vi.fn();
    const onError = vi.fn();
    const session = new LiveGridSession(live, onPush, onError, 1000);
    return { session, events, requests, onPush, onError };
  }

  it('waits for the first answer, however long it takes (a subscribe is always answered)', async () => {
    const { session, events } = make();
    let result: any = 'pending';
    session.fetch({ queryId: 'q', skip: 0, take: 10 }).then(r => (result = r));
    vi.advanceTimersByTime(60_000);
    await Promise.resolve();
    expect(result).toBe('pending');

    events.next({ type: 'result', result: page('a') });
    await Promise.resolve();
    expect(result.items.map((i: any) => i.id)).toEqual(['a']);
  });

  it('serves the request in force from memory, and a push nobody waited for asks for a re-fetch', async () => {
    const { session, events, requests, onPush } = make();
    const first = session.fetch({ queryId: 'q', skip: 0, take: 10 });
    events.next({ type: 'result', result: page('a') });
    await first;
    expect(onPush).not.toHaveBeenCalled();

    events.next({ type: 'result', result: page('a', 'b') });
    expect(onPush).toHaveBeenCalledTimes(1);
    expect((await session.fetch({ take: 10, skip: 0, queryId: 'q' }))!.items).toHaveLength(2);
    expect(requests).toHaveLength(1);
  });

  /**
   * The server sends nothing when nothing visible changed, an `update` included: a new sort over an
   * identical page is never answered, and the page held is then the right one.
   */
  it('settles an unanswered update on the page it holds', async () => {
    const { session, events } = make();
    const first = session.fetch({ queryId: 'q', skip: 0, take: 10 });
    events.next({ type: 'result', result: page('a') });
    await first;

    let result: any = 'pending';
    session.fetch({ queryId: 'q', skip: 0, take: 10, search: 'a' }).then(r => (result = r));
    vi.advanceTimersByTime(999);
    await Promise.resolve();
    expect(result).toBe('pending');
    vi.advanceTimersByTime(1);
    await Promise.resolve();
    expect(result.items.map((i: any) => i.id)).toEqual(['a']);
  });

  it('an error resolves the waiting fetch empty, reports once, and every later fetch is empty', async () => {
    const { session, events, requests, onError } = make();
    const first = session.fetch({ queryId: 'q', skip: 0, take: 10 });
    events.next({ type: 'error', code: 'failed', message: 'boom' });

    expect(await first).toBeNull();
    expect(onError).toHaveBeenCalledWith({ type: 'error', code: 'failed', message: 'boom' });
    expect(await session.fetch({ queryId: 'q', skip: 10, take: 10 })).toBeNull();
    expect(requests).toHaveLength(1);
  });
});
