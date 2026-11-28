import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SPARK_REVEAL_EVENT } from '@mintplayer/ng-spark/models';
import { SPARK_SECTION_REVEALED_CLASS, SparkViewportScroller } from './spark-viewport-scroller';

/**
 * #721, Part E: the scroller the router's anchor scrolling calls. jsdom has no layout, so the shell's scroll container
 * and the section are given the geometry a browser would: `#spark-main` starts 50 px down the viewport, its sticky
 * action bar is 54 px tall, and the section sits `sectionAt` px into the scrolled content.
 */
describe('SparkViewportScroller (#721)', () => {
  let scroller: SparkViewportScroller;
  let main: HTMLElement;
  let section: HTMLElement;
  let scrollTop: number;
  let sectionAt: number;
  let scrollTo: ReturnType<typeof vi.fn>;

  function place(element: HTMLElement, top: () => number, height = 0) {
    element.getBoundingClientRect = () => ({ top: top(), height, left: 0, right: 0, bottom: top() + height, width: 0, x: 0, y: top(), toJSON: () => ({}) }) as DOMRect;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [SparkViewportScroller] });
    scroller = TestBed.inject(SparkViewportScroller);

    scrollTop = 0;
    sectionAt = 600;
    main = document.createElement('main');
    main.id = 'spark-main';
    Object.defineProperty(main, 'scrollTop', { get: () => scrollTop, configurable: true });
    Object.defineProperty(main, 'scrollHeight', { get: () => 5000, configurable: true });
    Object.defineProperty(main, 'clientHeight', { get: () => 800, configurable: true });
    scrollTo = vi.fn((options: ScrollToOptions) => { scrollTop = options.top ?? scrollTop; });
    main.scrollTo = scrollTo as unknown as typeof main.scrollTo;
    place(main, () => 50);

    const bar = document.createElement('div');
    bar.className = 'spark-actionbar';
    place(bar, () => 50, 54);
    section = document.createElement('section');
    section.id = 'career';
    place(section, () => 50 + sectionAt - scrollTop);
    main.append(bar, section);
    document.body.append(main);
  });

  afterEach(() => {
    scroller.cancel();
    document.body.innerHTML = '';
    vi.restoreAllMocks();
  });

  it('reveals the section, then scrolls #spark-main to it below the sticky action bar', async () => {
    const revealed: EventTarget[] = [];
    main.addEventListener(SPARK_REVEAL_EVENT, event => revealed.push(event.target!));

    scroller.scrollToAnchor('career');

    // 600 into the content, less the 54 px bar and the 8 px gap.
    await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 538, behavior: 'smooth' })));
    expect(revealed).toEqual([section]);
    expect(scroller.anchorRequests).toBe(1);
  });

  it('waits for a section that renders later', async () => {
    scroller.scrollToAnchor('people');
    await new Promise(resolve => setTimeout(resolve, 30));
    expect(scrollTo).not.toHaveBeenCalled();

    const people = document.createElement('section');
    place(people, () => 50 + 1000 - scrollTop);
    main.append(people);
    people.id = 'people';

    await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 938 })));
  });

  it('stops waiting when cancelled: the next navigation', async () => {
    scroller.scrollToAnchor('people');
    scroller.cancel();

    const people = document.createElement('section');
    people.id = 'people';
    main.append(people);
    await new Promise(resolve => setTimeout(resolve, 80));

    expect(scrollTo).not.toHaveBeenCalled();
  });

  it('keeps the section in place while a card above it grows', async () => {
    scroller.scrollToAnchor('career');
    await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledTimes(1));

    sectionAt = 1400;

    await vi.waitFor(() => expect(scrollTo).toHaveBeenLastCalledWith(expect.objectContaining({ top: 1338 })));
  });

  it('lets go at the user\'s first wheel', async () => {
    scroller.scrollToAnchor('career');
    await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledTimes(1));

    main.dispatchEvent(new Event('wheel'));
    sectionAt = 1400;
    await new Promise(resolve => setTimeout(resolve, 80));

    expect(scrollTo).toHaveBeenCalledTimes(1);
  });

  it('outlines the section for a moment', async () => {
    scroller.scrollToAnchor('career');
    await vi.waitFor(() => expect(section.classList.contains(SPARK_SECTION_REVEALED_CLASS)).toBe(true));

    await vi.waitFor(() => expect(section.classList.contains(SPARK_SECTION_REVEALED_CLASS)).toBe(false), { timeout: 2500 });
  });

  it('uses the app\'s offset instead of the action bar\'s', async () => {
    scroller.setOffset([0, 100]);
    scroller.scrollToAnchor('career');

    await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 500 })));
  });

  it('jumps instead of gliding when the user prefers reduced motion', async () => {
    // jsdom has no matchMedia at all.
    window.matchMedia = vi.fn((query: string) => ({ matches: query.includes('reduce') }) as MediaQueryList);
    try {
      scroller.scrollToAnchor('career');

      await vi.waitFor(() => expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 538, behavior: 'instant' })));
    } finally {
      delete (window as { matchMedia?: unknown }).matchMedia;
    }
  });

  it('scrolls the window on a page without the shell', async () => {
    document.body.innerHTML = '';
    const target = document.createElement('section');
    target.id = 'career';
    place(target, () => 300);
    document.body.append(target);
    const windowScrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);

    scroller.scrollToAnchor('career');

    await vi.waitFor(() => expect(windowScrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 292 })));
  });

  it('reads and sets the position of #spark-main', () => {
    scroller.scrollToPosition([0, 420]);
    expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ left: 0, top: 420 }));
    expect(scroller.getScrollPosition()).toEqual([0, 420]);
  });
});
