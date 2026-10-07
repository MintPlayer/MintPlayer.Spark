import { Component, forwardRef, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { BsPriorityNavComponent } from '@mintplayer/ng-bootstrap/priority-nav';
import { SparkPriorityNavCloseOnActionDirective } from './spark-priority-nav-close.directive';

/**
 * A stand-in for `bs-priority-nav` with its DOM shape (strip toggle + `.priority-nav-overflow`) and
 * its `isMoreOpen` signal. The real nav measures its items, which jsdom cannot do, and the defect
 * was never in that part: the overflow simply had no "close on pick".
 */
@Component({
  selector: 'bs-priority-nav',
  template: `
    <button type="button" class="priority-nav-more-toggle" (click)="isMoreOpen.set(!isMoreOpen())">More</button>
    <div class="priority-nav-overflow">
      <span class="priority-nav-overflow-item"><button type="button" id="action"><span id="action-label">Add a passkey</span></button></span>
      <span class="priority-nav-overflow-item"><button type="button" id="nested" aria-expanded="false">Nested menu</button></span>
      <span class="priority-nav-overflow-item"><span id="text">Not a control</span></span>
    </div>`,
  providers: [{ provide: BsPriorityNavComponent, useExisting: forwardRef(() => StubPriorityNav) }],
})
class StubPriorityNav {
  readonly isMoreOpen = signal(true);
}

@Component({
  imports: [StubPriorityNav, SparkPriorityNavCloseOnActionDirective],
  template: `<bs-priority-nav sparkCloseOnAction />`,
})
class Host {}

function render() {
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const nav = fixture.debugElement.children[0].injector.get(StubPriorityNav);
  return { el, nav };
}

describe('SparkPriorityNavCloseOnActionDirective', () => {
  it('closes the "More" overflow when an action inside it is clicked', () => {
    const { el, nav } = render();
    (el.querySelector('#action-label') as HTMLElement).click();
    expect(nav.isMoreOpen()).toBe(false);
  });

  it('leaves it open for a control that opens a menu of its own', () => {
    const { el, nav } = render();
    (el.querySelector('#nested') as HTMLElement).click();
    expect(nav.isMoreOpen()).toBe(true);
  });

  it('leaves it open for a click on non-interactive overflow content', () => {
    const { el, nav } = render();
    (el.querySelector('#text') as HTMLElement).click();
    expect(nav.isMoreOpen()).toBe(true);
  });

  it('does not fight the toggle, which lives outside the overflow', () => {
    const { el, nav } = render();
    nav.isMoreOpen.set(false);
    (el.querySelector('.priority-nav-more-toggle') as HTMLElement).click();
    expect(nav.isMoreOpen()).toBe(true);
  });
});
