import { describe, expect, it } from 'vitest';
import { RetryActionService } from './retry-action.service';
import { RetryActionPayload } from '@mintplayer/ng-spark/models';

const payload: RetryActionPayload = {
  step: 'confirm-overwrite',
  title: 'Overwrite?',
  message: 'A newer version exists.',
  options: ['Overwrite', 'Cancel'],
  defaultOption: 'Cancel',
  persistentObject: { id: 'p/1', name: 'Person', objectTypeId: 't/1', attributes: [] } as any,
};

describe('RetryActionService', () => {
  it('payload signal starts as null', () => {
    const service = new RetryActionService();
    expect(service.payload()).toBeNull();
  });

  it('show() sets the payload signal and returns a pending promise', () => {
    const service = new RetryActionService();

    const promise = service.show(payload);

    expect(service.payload()).toBe(payload);
    expect(promise).toBeInstanceOf(Promise);
  });

  it('respond() resolves the show() promise with the result and clears the payload', async () => {
    const service = new RetryActionService();

    const promise = service.show(payload);
    service.respond({ step: payload.step, option: 'Overwrite', persistentObject: payload.persistentObject });

    const result = await promise;
    expect(result.option).toBe('Overwrite');
    expect(result.step).toBe('confirm-overwrite');
    expect(service.payload()).toBeNull();
  });

  it('respond() before show() does not throw', () => {
    const service = new RetryActionService();

    expect(() =>
      service.respond({ step: 'x', option: 'Cancel', persistentObject: {} as any }),
    ).not.toThrow();
  });

  it('a second show() supersedes the first payload', () => {
    const service = new RetryActionService();
    service.show(payload);

    const second: RetryActionPayload = { ...payload, step: 'second' };
    service.show(second);

    expect(service.payload()).toBe(second);
  });
});
