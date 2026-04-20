import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkRetryActionModalComponent } from './spark-retry-action-modal.component';
import { RetryActionService } from '@mintplayer/ng-spark/services';
import { RetryActionPayload } from '@mintplayer/ng-spark/models';

const samplePayload: RetryActionPayload = {
  step: 'confirm-overwrite',
  title: 'Overwrite the existing record?',
  message: 'This will replace the current values.',
  options: ['Overwrite', 'Cancel'],
  defaultOption: 'Cancel',
  persistentObject: { id: 'p/1', name: 'Person', objectTypeId: 't/1', attributes: [] } as any,
};

describe('SparkRetryActionModalComponent', () => {
  let service: RetryActionService;
  let component: SparkRetryActionModalComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SparkRetryActionModalComponent] });
    service = TestBed.inject(RetryActionService);
    const fixture = TestBed.createComponent(SparkRetryActionModalComponent);
    component = fixture.componentInstance;
  });

  it('isOpen() is false when no payload is set', () => {
    expect(component.isOpen()).toBe(false);
  });

  it('isOpen() becomes true once the service publishes a payload', () => {
    service.show(samplePayload);

    expect(component.isOpen()).toBe(true);
  });

  it('onOption() forwards the choice to RetryActionService and clears the payload', async () => {
    const promise = service.show(samplePayload);

    component.onOption('Overwrite');

    const result = await promise;
    expect(result.option).toBe('Overwrite');
    expect(result.step).toBe('confirm-overwrite');
    expect(service.payload()).toBeNull();
  });

  it('onOption() does nothing when no payload is active (no throw, no resolution)', () => {
    expect(() => component.onOption('Cancel')).not.toThrow();
  });
});
