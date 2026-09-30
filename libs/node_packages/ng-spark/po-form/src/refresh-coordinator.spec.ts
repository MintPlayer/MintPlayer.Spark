import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PersistentObject, RefreshTrigger } from '@mintplayer/ng-spark/models';

import {
  RefreshCoordinator,
  RefreshTriggerShape,
  SPARK_REFRESH_DEBOUNCE_MS,
  effectiveTrigger,
  isDiscreteEditor,
  refreshDispatch,
  triggersImmediately,
} from './refresh-coordinator';

/** One representative shape per editor kind the rule distinguishes. */
const discreteEditors: Record<string, RefreshTriggerShape> = {
  lookup: { dataType: 'string', lookupReferenceType: 'CarStatus' },
  reference: { dataType: 'Reference' },
  lookupReference: { dataType: 'LookupReference' },
  boolean: { dataType: 'boolean' },
  bool: { dataType: 'bool' },
  date: { dataType: 'date' },
  datetime: { dataType: 'datetime' },
  dateonly: { dataType: 'dateonly' },
  enum: { dataType: 'enum' },
  color: { dataType: 'color' },
};

const freeTextEditors: Record<string, RefreshTriggerShape> = {
  string: { dataType: 'string' },
  number: { dataType: 'number' },
  decimal: { dataType: 'decimal' },
  absent: {},
};

const triggers: (RefreshTrigger | undefined)[] = [undefined, 'None', 'Auto', 'ValueChanged', 'Blur'];

describe('effectiveTrigger', () => {
  const expectedDiscrete: Record<string, string> = {
    undefined: 'none', None: 'none', Auto: 'change', ValueChanged: 'change', Blur: 'change',
  };
  const expectedFreeText: Record<string, string> = {
    undefined: 'none', None: 'none', Auto: 'blur', ValueChanged: 'change', Blur: 'blur',
  };

  for (const [kind, shape] of Object.entries(discreteEditors)) {
    for (const trigger of triggers) {
      it(`${String(trigger)} on a ${kind} editor → ${expectedDiscrete[String(trigger)]}`, () => {
        expect(isDiscreteEditor(shape)).toBe(true);
        expect(effectiveTrigger({ ...shape, triggersRefresh: trigger })).toBe(expectedDiscrete[String(trigger)]);
      });
    }
  }

  for (const [kind, shape] of Object.entries(freeTextEditors)) {
    for (const trigger of triggers) {
      it(`${String(trigger)} on a ${kind} (free-text) editor → ${expectedFreeText[String(trigger)]}`, () => {
        expect(isDiscreteEditor(shape)).toBe(false);
        expect(effectiveTrigger({ ...shape, triggersRefresh: trigger })).toBe(expectedFreeText[String(trigger)]);
      });
    }
  }

  it('treats a missing attribute as no trigger', () => {
    expect(effectiveTrigger(undefined)).toBe('none');
    expect(refreshDispatch(undefined)).toBeNull();
  });
});

describe('refreshDispatch', () => {
  it('sends a change on a discrete editor immediately, whatever the declared trigger', () => {
    for (const trigger of ['Auto', 'ValueChanged', 'Blur'] as const) {
      expect(refreshDispatch({ dataType: 'boolean', triggersRefresh: trigger })).toBe('immediate');
      expect(triggersImmediately({ dataType: 'boolean', triggersRefresh: trigger })).toBe(true);
    }
  });

  it('debounces ValueChanged free text and leaves Auto / Blur free text to the blur', () => {
    expect(refreshDispatch({ dataType: 'string', triggersRefresh: 'ValueChanged' })).toBe('debounced');
    expect(refreshDispatch({ dataType: 'string', triggersRefresh: 'Auto' })).toBe('blur');
    expect(refreshDispatch({ dataType: 'string', triggersRefresh: 'Blur' })).toBe('blur');
    expect(triggersImmediately({ dataType: 'string', triggersRefresh: 'ValueChanged' })).toBe(false);
  });

  it('sends nothing for None or absent', () => {
    expect(refreshDispatch({ dataType: 'boolean', triggersRefresh: 'None' })).toBeNull();
    expect(refreshDispatch({ dataType: 'boolean' })).toBeNull();
  });
});

describe('RefreshCoordinator debounce', () => {
  let send: ReturnType<typeof vi.fn<(triggeredBy: string) => Promise<PersistentObject>>>;
  let coordinator: RefreshCoordinator;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    send = vi.fn<(triggeredBy: string) => Promise<PersistentObject>>().mockResolvedValue({ attributes: [] } as unknown as PersistentObject);
    coordinator = new RefreshCoordinator({
      send,
      currentValues: () => ({}),
      apply: () => undefined,
      setBusy: () => undefined,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  /** Lets the coordinator's promise queue run after a timer or a call. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) await Promise.resolve();
  }

  it('coalesces a burst of changes into one request once typing pauses', async () => {
    for (let i = 0; i < 5; i++) {
      void coordinator.dispatch('Name', 'debounced');
      vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS - 1);
    }
    await settle();
    expect(send).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    await settle();
    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith('Name');

    // Nothing is left over to fire a second time.
    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS * 2);
    await settle();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('debounces each path on its own', async () => {
    void coordinator.trigger('A', { debounceMs: SPARK_REFRESH_DEBOUNCE_MS });
    void coordinator.trigger('B', { debounceMs: SPARK_REFRESH_DEBOUNCE_MS });
    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS);
    await settle();

    expect(send.mock.calls.map(c => c[0]).sort()).toEqual(['A', 'B']);
  });

  it('is flushed immediately by a blur, and the timer does not fire again', async () => {
    void coordinator.dispatch('Name', 'debounced');
    await coordinator.blur('Name');
    expect(send).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS);
    await settle();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('is flushed immediately by save (flush), and the timer does not fire again', async () => {
    void coordinator.dispatch('Name', 'debounced');
    void coordinator.dispatch('Plate', 'blur');
    await coordinator.flush();
    expect(send.mock.calls.map(c => c[0]).sort()).toEqual(['Name', 'Plate']);

    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS);
    await settle();
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('is superseded by an immediate send of the same path', async () => {
    void coordinator.dispatch('Name', 'debounced');
    await coordinator.dispatch('Name', 'immediate');
    expect(send).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS);
    await settle();
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('marks a blur dispatch pending without sending until blur', async () => {
    await coordinator.dispatch('Name', 'blur');
    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS * 2);
    await settle();
    expect(send).not.toHaveBeenCalled();

    await coordinator.blur('Name');
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('drops a waiting debounce on dispose', async () => {
    void coordinator.dispatch('Name', 'debounced');
    coordinator.dispose();
    vi.advanceTimersByTime(SPARK_REFRESH_DEBOUNCE_MS);
    await settle();
    await coordinator.flush();
    expect(send).not.toHaveBeenCalled();
  });
});
