import { NO_ERRORS_SCHEMA } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import QuerySlotsComponent from './query-slots.component';

describe('QuerySlotsComponent', () => {
  it('sits in ng-bootstrap\'s container (D84)', () => {
    // The cards load their queries from the server; this spec looks only at the page around them.
    TestBed.overrideComponent(QuerySlotsComponent, {
      remove: { imports: [SparkQueryCardComponent] },
      add: { schemas: [NO_ERRORS_SCHEMA] },
    });
    const fixture = TestBed.createComponent(QuerySlotsComponent);
    fixture.detectChanges();

    // Bootstrap's .container rules ship with <bs-container>: a plain .container stayed full width, without its gutter.
    // The page's own one, not the one <bs-grid> renders further down.
    expect((fixture.nativeElement as HTMLElement).querySelector(':scope > bs-container > .container.py-4')).not.toBeNull();
  });
});
