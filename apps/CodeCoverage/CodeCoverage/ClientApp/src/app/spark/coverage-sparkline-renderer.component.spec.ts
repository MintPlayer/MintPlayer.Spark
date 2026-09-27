import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { QueryResultItem } from '@mintplayer/ng-spark/models';
import { BsSparklineComponent } from '@mintplayer/ng-bootstrap/charts/sparkline';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle, StubSparkline } from '../../testing/test-utils';
import { clearSparklineCacheForTesting, CoverageSparklineRendererComponent } from './coverage-sparkline-renderer.component';

function row(values: Record<string, unknown>): QueryResultItem {
  return {
    id: 'items/1',
    values: Object.entries(values).map(([key, value]) => ({ key, value })),
  } as unknown as QueryResultItem;
}

/** Two cells in one host: a grid renders one renderer per row, and they share the owner's batch. */
@Component({
  selector: 'app-two-cells',
  imports: [CoverageSparklineRendererComponent],
  template: `
    <app-coverage-sparkline-renderer [value]="'acme/widgets'" [item]="rowA" />
    <app-coverage-sparkline-renderer [value]="'acme/gadgets'" [item]="rowB" />
  `,
})
class TwoCellsHost {
  readonly rowA = row({ OwnerKey: 'github:acme' });
  readonly rowB = row({ OwnerKey: 'github:acme' });
}

describe('CoverageSparklineRendererComponent', () => {
  let browse: BrowseServiceStub;

  function configure(getSparklines: (...args: any[]) => any): void {
    browse = createBrowseStub({ getSparklines });
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideBrowseStub(browse)],
    });
    TestBed.overrideComponent(CoverageSparklineRendererComponent, {
      remove: { imports: [BsSparklineComponent] },
      add: { imports: [StubSparkline] },
    });
  }

  async function render(inputs: Record<string, unknown>): Promise<ComponentFixture<CoverageSparklineRendererComponent>> {
    const fixture = TestBed.createComponent(CoverageSparklineRendererComponent);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    await settle(fixture);
    return fixture;
  }

  function sparklines(fixture: ComponentFixture<unknown>): StubSparkline[] {
    return fixture.debugElement.queryAll(By.directive(StubSparkline)).map(d => d.componentInstance as StubSparkline);
  }

  beforeEach(() => clearSparklineCacheForTesting());
  afterEach(() => clearSparklineCacheForTesting());

  it('fetches the owner batch once and shares it across every cell of that owner', async () => {
    configure(() => Promise.resolve({ 'acme/widgets': [10, 20, 30], 'acme/gadgets': [50, 60] }));

    const fixture = TestBed.createComponent(TwoCellsHost);
    await settle(fixture);

    expect(browse.getSparklines).toHaveBeenCalledTimes(1);
    expect(browse.getSparklines).toHaveBeenCalledWith('github', 'acme');
    expect(sparklines(fixture).map(s => s.points())).toEqual([[10, 20, 30], [50, 60]]);
  });

  it('keeps the memo across component instances until it is cleared', async () => {
    configure(() => Promise.resolve({ 'acme/widgets': [1, 2] }));

    await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }) });
    await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }) });

    expect(browse.getSparklines).toHaveBeenCalledTimes(1);
  });

  // The memo key is provider AND owner: a same-named owner on another forge is another account.
  it('takes the provider from the OwnerKey prefix and keys the memo on it', async () => {
    configure((provider: string) => Promise.resolve({ 'acme/widgets': provider === 'gitlab' ? [5, 6] : [7, 8] }));

    const gitlab = await render({ value: 'acme/widgets', item: row({ OwnerKey: 'gitlab:acme' }) });
    const github = await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }) });

    expect(browse.getSparklines.mock.calls).toEqual([['gitlab', 'acme'], ['github', 'acme']]);
    expect(sparklines(gitlab)[0].points()).toEqual([5, 6]);
    expect(sparklines(github)[0].points()).toEqual([7, 8]);
  });

  it('binds the fixed 0-100 scale and a labelled trend', async () => {
    configure(() => Promise.resolve({ 'acme/widgets': [1, 2] }));

    const [spark] = sparklines(await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }) }));

    expect(spark.yMin()).toBe(0);
    expect(spark.yMax()).toBe(100);
    expect(spark.inputLabel()).toBe('Coverage trend for acme/widgets');
  });

  it('falls back to options.provider only when the row has no forge', async () => {
    configure(() => Promise.resolve({ 'acme/widgets': [1, 2] }));

    await render({ value: 'acme/widgets', item: row({}), options: { provider: 'bitbucket' } });
    await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }), options: { provider: 'bitbucket' } });

    expect(browse.getSparklines.mock.calls).toEqual([['bitbucket', 'acme'], ['github', 'acme']]);
  });

  // No guessing: a sparkline for a guessed forge would be someone else's coverage.
  it('draws the dash and fetches nothing when no forge is known', async () => {
    configure(() => Promise.resolve({}));

    for (const inputs of [
      { value: 'acme/widgets' },
      { value: 'acme/widgets', item: row({ OwnerKey: 'acme' }) },
      { value: 'acme/widgets', item: row({}), options: { provider: '' } },
    ]) {
      const fixture = await render(inputs);
      expect(sparklines(fixture)).toHaveLength(0);
      expect((fixture.nativeElement as HTMLElement).textContent!.trim()).toBe('—');
    }
    expect(browse.getSparklines).not.toHaveBeenCalled();
  });

  it('draws the dash and fetches nothing for a non-string or empty value', async () => {
    configure(() => Promise.resolve({}));

    for (const value of [null, 42, '', '/widgets']) {
      const fixture = await render({ value, item: row({ OwnerKey: 'github:acme' }) });
      expect((fixture.nativeElement as HTMLElement).textContent!.trim()).toBe('—');
    }
    expect(browse.getSparklines).not.toHaveBeenCalled();
  });

  it('draws the dash for a repository missing from the batch or with a single point', async () => {
    configure(() => Promise.resolve({ 'acme/widgets': [42] }));

    for (const value of ['acme/widgets', 'acme/other']) {
      const fixture = await render({ value, item: row({ OwnerKey: 'github:acme' }) });
      expect(sparklines(fixture)).toHaveLength(0);
      expect((fixture.nativeElement as HTMLElement).textContent!.trim()).toBe('—');
    }
    expect(browse.getSparklines).toHaveBeenCalledTimes(1);
  });

  // A failed batch is memoised as empty, so a grid of 50 rows does not retry 50 times.
  it('treats a failed batch as no data, and does not refetch it', async () => {
    configure(() => Promise.reject(new Error('503')));

    const first = await render({ value: 'acme/widgets', item: row({ OwnerKey: 'github:acme' }) });
    await render({ value: 'acme/gadgets', item: row({ OwnerKey: 'github:acme' }) });

    expect((first.nativeElement as HTMLElement).textContent!.trim()).toBe('—');
    expect(browse.getSparklines).toHaveBeenCalledTimes(1);
  });
});
