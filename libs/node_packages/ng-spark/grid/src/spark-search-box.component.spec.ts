import { TestBed, ComponentFixture } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkSearchBoxComponent, SPARK_SEARCH_DEBOUNCE_MS } from './spark-search-box.component';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/**
 * The shared query search box (#460 M15, D17 addendum): the query-list page and the sub-query
 * card's header both render it, so these cases are the behaviour of both.
 */
describe('SparkSearchBoxComponent', () => {
  let fixture: ComponentFixture<SparkSearchBoxComponent>;

  function input(): HTMLInputElement {
    return fixture.nativeElement.querySelector('input.spark-search-input');
  }

  function type(value: string): void {
    const el = input();
    el.value = value;
    el.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function clearButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.spark-search-clear');
  }

  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: SparkLanguageService, useValue: { t: (k: string) => k } }],
    });
    fixture = TestBed.createComponent(SparkSearchBoxComponent);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => vi.useRealTimers());

  it('publishes the term once typing pauses, not per keystroke', () => {
    const published: string[] = [];
    fixture.componentInstance.term.subscribe(t => published.push(t));

    type('a');
    type('al');
    type('ali');
    expect(published).toEqual([]);

    vi.advanceTimersByTime(SPARK_SEARCH_DEBOUNCE_MS);
    expect(published).toEqual(['ali']);
    expect(fixture.componentInstance.term()).toBe('ali');
  });

  it('shows the clear button only while there is text', () => {
    expect(clearButton()).toBeNull();

    type('ali');
    expect(clearButton()).not.toBeNull();
  });

  it('clears at once, cancelling a pending term', async () => {
    type('ali');
    vi.advanceTimersByTime(SPARK_SEARCH_DEBOUNCE_MS);
    type('alice');

    clearButton()!.click();
    fixture.detectChanges();
    vi.advanceTimersByTime(SPARK_SEARCH_DEBOUNCE_MS);
    // ngModel writes the view value on a microtask.
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.term()).toBe('');
    expect(input().value).toBe('');
    expect(clearButton()).toBeNull();
  });

  it('publishes an emptied box at once, like the clear button', () => {
    type('ali');
    vi.advanceTimersByTime(SPARK_SEARCH_DEBOUNCE_MS);

    type('');
    expect(fixture.componentInstance.term()).toBe('');
  });

  it('Escape clears', () => {
    type('ali');
    vi.advanceTimersByTime(SPARK_SEARCH_DEBOUNCE_MS);

    input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.term()).toBe('');
  });

  it('shows a term set from outside', async () => {
    fixture.componentRef.setInput('term', 'bob');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(input().value).toBe('bob');
    expect(clearButton()).not.toBeNull();
  });

  it('a debounce of 0 publishes on every keystroke', () => {
    fixture.componentRef.setInput('debounce', 0);
    type('a');

    expect(fixture.componentInstance.term()).toBe('a');
  });
});
