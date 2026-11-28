import { DOCUMENT, ViewportScroller, isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { SPARK_REVEAL_EVENT } from '@mintplayer/ng-spark/models';

/** The id of Spark's shell scroll container: the shell scrolls `<main id="spark-main">`, never the window. */
export const SPARK_MAIN_ID = 'spark-main';

/** The class a section holds for a moment after a `#fragment` link scrolled to it (`styles/_section-reveal.scss`). */
export const SPARK_SECTION_REVEALED_CLASS = 'spark-section-revealed';

/** How long the scroller waits for an anchor that is not on the page yet: an object page renders after its data. */
const WAIT_FOR_ANCHOR_MS = 10_000;
/** The section is held in place until it has not moved for this long... */
const SETTLED_AFTER_MS = 1_000;
/** ...and never longer than this. */
const HOLD_AT_MOST_MS = 5_000;
/** How long a revealed section keeps its outline (Dec5). */
const OUTLINE_MS = 1_500;
/** The gap between the sticky action bar and the section scrolled to, in px. */
const GAP_PX = 8;
/** What the user does that ends the holding: the page is theirs again. */
const USER_INPUT = ['wheel', 'touchstart', 'pointerdown', 'keydown'] as const;

/** What scrolls: Spark's `#spark-main`, or the window on a page without the shell. */
interface Scrollport {
  /** Where the scrolled content's top edge is in the viewport, at scroll position 0 subtracted. */
  originTop(): number;
  scrollLeft(): number;
  scrollTop(): number;
  maxScrollTop(): number;
  scrollTo(options: ScrollToOptions): void;
  /** Where Spark's sticky action bar is looked for, and the user's input listened to. */
  readonly root: ParentNode & EventTarget;
}

/**
 * Spark's {@link ViewportScroller} (#721, `docs/section_fragment_links_PRD.md` Part E, Dec8/Dec9): what the router's
 * anchor scrolling calls when a URL has a `#fragment`. Registered by `withFragmentScrolling()`.
 *
 * Angular's own scroller scrolls the window, which never scrolls in Spark's shell. This one scrolls `#spark-main` when the
 * shell is on the page (and the section is in it), else the window. For an anchor it:
 * 1. waits for the anchor when it is not on the page yet, up to 10 s, cancelled by the next navigation;
 * 2. dispatches a bubbling `spark-reveal` event from it, so the object page opens the tab that holds it;
 * 3. scrolls it to the top, below Spark's sticky action bar (`setOffset` replaces that offset), smoothly unless the user
 *    prefers reduced motion;
 * 4. keeps it there while the page still settles (a sub-query card above it loading its rows), until it has not moved
 *    for 1 s, for 5 s at most, or until the user wheels, touches, clicks or types;
 * 5. outlines it for about 1.5 s.
 *
 * On the server every method does nothing.
 */
@Injectable()
export class SparkViewportScroller extends ViewportScroller {
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private offset: (() => [number, number]) | null = null;
  private run: AbortController | null = null;
  private outlined: { element: Element; timer: ReturnType<typeof setTimeout> } | null = null;

  /** How many anchors were asked for: `SparkFragmentScrolling` reads it to tell whether the router asked. */
  anchorRequests = 0;

  override setOffset(offset: [number, number] | (() => [number, number])): void {
    this.offset = Array.isArray(offset) ? () => offset : offset;
  }

  override getScrollPosition(): [number, number] {
    if (!this.browser) return [0, 0];
    const port = this.scrollport();
    return [port.scrollLeft(), port.scrollTop()];
  }

  override scrollToPosition(position: [number, number], options?: ScrollOptions): void {
    if (!this.browser) return;
    this.cancel();
    this.scrollport().scrollTo({ ...options, left: position[0], top: position[1] });
  }

  override scrollToAnchor(anchor: string, options?: ScrollOptions): void {
    if (!this.browser) return;
    this.anchorRequests++;
    this.cancel();
    const run = new AbortController();
    this.run = run;
    void this.reveal(anchor, options, run.signal);
  }

  override setHistoryScrollRestoration(scrollRestoration: 'auto' | 'manual'): void {
    if (!this.browser) return;
    try {
      this.document.defaultView!.history.scrollRestoration = scrollRestoration;
    } catch {
      // A sandboxed iframe refuses it; Angular's own scroller only warns.
    }
  }

  /** Stops waiting for, or holding, the anchor asked for last. The next navigation calls it. */
  cancel(): void {
    this.run?.abort();
    this.run = null;
  }

  private async reveal(anchor: string, options: ScrollOptions | undefined, signal: AbortSignal): Promise<void> {
    const element = this.find(anchor) ?? await this.waitFor(anchor, signal);
    if (!element || signal.aborted) return;

    // The tab first: a section on a hidden tab has no box to scroll to. Two frames let the page draw the tab.
    element.dispatchEvent(new CustomEvent(SPARK_REVEAL_EVENT, { bubbles: true, composed: true }));
    await this.nextFrame();
    await this.nextFrame();
    if (signal.aborted) return;

    this.outline(element);
    element.focus?.({ preventScroll: true });
    this.hold(element, options, signal);
  }

  /** As Angular's scroller: the element with that id, else the first with that name. */
  private find(anchor: string): HTMLElement | null {
    return this.document.getElementById(anchor) ?? (this.document.getElementsByName(anchor)[0] as HTMLElement | undefined) ?? null;
  }

  private waitFor(anchor: string, signal: AbortSignal): Promise<HTMLElement | null> {
    return new Promise(resolve => {
      const done = (element: HTMLElement | null) => {
        observer.disconnect();
        clearTimeout(timer);
        signal.removeEventListener('abort', onAbort);
        resolve(element);
      };
      const onAbort = () => done(null);
      const observer = new MutationObserver(() => {
        const element = this.find(anchor);
        if (element) done(element);
      });
      // An id is often bound after its element is inserted ([attr.id]), hence the attribute filter.
      observer.observe(this.document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['id', 'name'] });
      // An unknown fragment does nothing, silently.
      const timer = setTimeout(() => done(null), WAIT_FOR_ANCHOR_MS);
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  private hold(element: HTMLElement, options: ScrollOptions | undefined, signal: AbortSignal): void {
    const port = this.scrollportOf(element);
    const behavior: ScrollBehavior = options?.behavior ?? (this.prefersReducedMotion() ? 'instant' : 'smooth');
    // In content coordinates, so it changes only when the page's layout does, never while it scrolls.
    const wanted = () => Math.max(0, port.scrollTop() + element.getBoundingClientRect().top - port.originTop() - this.offsetIn(port)[1]);

    let target = wanted();
    port.scrollTo({ ...options, behavior, top: target });

    const started = performance.now();
    let lastMove = started;
    let lastScrollTop = port.scrollTop();
    let stillFrames = 0;
    const release = () => {
      if (this.run?.signal === signal) this.cancel();
    };
    for (const type of USER_INPUT)
      port.root.addEventListener(type, release, { passive: true, signal });

    const tick = () => {
      if (signal.aborted) return;
      const now = performance.now();
      if (now - started > HOLD_AT_MOST_MS || now - lastMove > SETTLED_AFTER_MS) {
        release();
        return;
      }
      const scrollTop = port.scrollTop();
      stillFrames = Math.abs(scrollTop - lastScrollTop) < 1 ? stillFrames + 1 : 0;
      lastScrollTop = scrollTop;
      if (stillFrames === 0) lastMove = now;

      const next = wanted();
      const reachable = Math.min(next, port.maxScrollTop());
      if (Math.abs(next - target) > 1) {
        // The section moved: a card above it loaded its rows.
        target = next;
        lastMove = now;
        port.scrollTo({ ...options, behavior, top: target });
      } else if (stillFrames >= 3 && Math.abs(scrollTop - reachable) > 1) {
        // The scroll stopped short: the page was not tall enough yet.
        lastMove = now;
        port.scrollTo({ ...options, behavior, top: reachable });
      }
      void this.nextFrame().then(tick);
    };
    void this.nextFrame().then(tick);
  }

  private outline(element: Element): void {
    if (this.outlined) {
      clearTimeout(this.outlined.timer);
      this.outlined.element.classList.remove(SPARK_SECTION_REVEALED_CLASS);
    }
    // A reflow between removing and adding restarts the fade when the same section is revealed again.
    void (element as HTMLElement).offsetWidth;
    element.classList.add(SPARK_SECTION_REVEALED_CLASS);
    const timer = setTimeout(() => {
      element.classList.remove(SPARK_SECTION_REVEALED_CLASS);
      if (this.outlined?.element === element) this.outlined = null;
    }, OUTLINE_MS);
    this.outlined = { element, timer };
  }

  /** The app's offset (`setOffset`), else the height of Spark's sticky action bar plus a small gap. */
  private offsetIn(port: Scrollport): [number, number] {
    if (this.offset) return this.offset();
    const bar = port.root.querySelector('.spark-actionbar');
    return [0, (bar?.getBoundingClientRect().height ?? 0) + GAP_PX];
  }

  private scrollport(): Scrollport {
    const main = this.document.getElementById(SPARK_MAIN_ID);
    return main ? elementScrollport(main) : windowScrollport(this.document);
  }

  private scrollportOf(element: Element): Scrollport {
    const main = this.document.getElementById(SPARK_MAIN_ID);
    return main?.contains(element) ? elementScrollport(main) : windowScrollport(this.document);
  }

  private prefersReducedMotion(): boolean {
    return this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }

  private nextFrame(): Promise<void> {
    const view = this.document.defaultView;
    return new Promise(resolve => {
      if (view?.requestAnimationFrame) view.requestAnimationFrame(() => resolve());
      else setTimeout(resolve, 16);
    });
  }
}

function elementScrollport(main: HTMLElement): Scrollport {
  return {
    originTop: () => main.getBoundingClientRect().top + main.clientTop,
    scrollLeft: () => main.scrollLeft,
    scrollTop: () => main.scrollTop,
    maxScrollTop: () => main.scrollHeight - main.clientHeight,
    scrollTo: options => main.scrollTo(options),
    root: main,
  };
}

function windowScrollport(document: Document): Scrollport {
  const view = document.defaultView!;
  return {
    originTop: () => 0,
    scrollLeft: () => view.scrollX,
    scrollTop: () => view.scrollY,
    maxScrollTop: () => document.documentElement.scrollHeight - view.innerHeight,
    scrollTo: options => view.scrollTo(options),
    root: document,
  };
}
