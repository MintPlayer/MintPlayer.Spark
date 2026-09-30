import { PersistentObject, RefreshOverlay, RefreshTrigger } from '@mintplayer/ng-spark/models';

/** What the coordinator needs from its host, so it can be tested without mounting a form. */
export interface RefreshCoordinatorHost {
  /** POSTs the object and resolves with the reshaped one. */
  send(triggeredBy: string): Promise<PersistentObject>;
  /** Values as they are right now — read at dispatch time to snapshot what is being sent. */
  currentValues(): Record<string, any>;
  /** Applies a settled response. Not called for a superseded one. */
  apply(response: PersistentObject, sent: Record<string, any>): void;
  /** Surfaced so the host can show a busy affordance. Never used to disable fields. */
  setBusy(busy: boolean): void;
}

/**
 * Serializes refreshes for **one** form instance and drops superseded ones.
 *
 * Per-instance rather than a service, deliberately. The retry-action modal renders its own
 * `spark-po-form`, and a refresh can carry a retry operation — so a refresh can open a modal
 * containing a form whose own attributes may trigger refreshes. A shared coordinator would let the
 * nested form resolve or supersede the outer form's pending request. The same applies to the
 * recursive `spark-po-form` used for modal AsDetail editing.
 *
 * Cancellation is not available: the service layer is promise-based (`firstValueFrom`), so a stale
 * response *will* arrive. It is discarded by sequence number rather than prevented.
 */
export class RefreshCoordinator {
  private queue: Promise<void> = Promise.resolve();
  private sequence = 0;
  private settled = 0;
  private pending = new Set<string>();
  /** Debounce timers for `ValueChanged` free text, keyed by path. A path here is also in `pending`. */
  private timers = new Map<string, ReturnType<typeof setTimeout>>();

  constructor(private readonly host: RefreshCoordinatorHost) {}

  /** Whether a refresh is in flight. */
  get isRefreshing(): boolean {
    return this.sequence !== this.settled;
  }

  /**
   * Marks `attributeName` as needing a refresh without sending one — for free-text editors, which
   * would otherwise issue a request per keystroke. Flushed by {@link blur} or {@link flush}.
   */
  markPending(attributeName: string): void {
    this.pending.add(attributeName);
  }

  /**
   * Routes a change by its {@link RefreshDispatch}: send now, send once typing pauses, or wait for
   * blur. The one entry point a form needs, so the three editors' policies live here.
   */
  dispatch(attributeName: string, mode: RefreshDispatch): Promise<void> {
    switch (mode) {
      case 'immediate':
        return this.trigger(attributeName);
      case 'debounced':
        return this.trigger(attributeName, { debounceMs: SPARK_REFRESH_DEBOUNCE_MS });
      case 'blur':
        this.markPending(attributeName);
        return Promise.resolve();
    }
  }

  /** Sends a pending refresh for `attributeName`, if one was marked (or is waiting on its debounce). */
  blur(attributeName: string): Promise<void> {
    this.clearTimer(attributeName);
    if (!this.pending.delete(attributeName)) return Promise.resolve();
    return this.trigger(attributeName);
  }

  /**
   * Sends every refresh still marked pending, including any waiting on a debounce. Called before
   * save, so a value typed and never blurred — the user tabbing straight to the save button — is
   * still reflected before the object goes.
   */
  async flush(): Promise<void> {
    for (const name of [...this.timers.keys()]) this.clearTimer(name);
    const names = [...this.pending];
    this.pending.clear();
    for (const name of names) await this.trigger(name);
    await this.queue;
  }

  /** Drops every pending mark and debounce timer without sending — the form is going away. */
  dispose(): void {
    for (const name of [...this.timers.keys()]) this.clearTimer(name);
    this.pending.clear();
  }

  /**
   * Sends a refresh — immediately for discrete editors, where every change is a committed one.
   *
   * With `debounceMs`, the send waits until `attributeName` has been quiet for that long; each call
   * restarts the wait, so a burst of keystrokes costs one request. Until it fires, the path counts as
   * pending, so {@link blur} and {@link flush} send it straight away instead of waiting out the timer.
   */
  trigger(attributeName: string, options?: { debounceMs?: number }): Promise<void> {
    const debounceMs = options?.debounceMs ?? 0;
    if (debounceMs > 0) {
      this.clearTimer(attributeName);
      this.pending.add(attributeName);
      this.timers.set(attributeName, setTimeout(() => {
        this.timers.delete(attributeName);
        if (this.pending.delete(attributeName)) void this.trigger(attributeName);
      }, debounceMs));
      return Promise.resolve();
    }

    // An immediate send covers anything this path still had waiting.
    this.clearTimer(attributeName);
    this.pending.delete(attributeName);

    const ticket = ++this.sequence;
    this.host.setBusy(true);

    this.queue = this.queue.then(async () => {
      const sent = { ...this.host.currentValues() };
      try {
        const response = await this.host.send(attributeName);

        // A newer refresh was dispatched while this one was in flight, so this response describes a
        // form state that no longer exists. Applying it would resurrect superseded metadata.
        if (ticket !== this.sequence) return;

        this.host.apply(response, sent);
      } finally {
        this.settled = Math.max(this.settled, ticket);
        if (!this.isRefreshing) this.host.setBusy(false);
      }
    });

    return this.queue;
  }

  private clearTimer(attributeName: string): void {
    const timer = this.timers.get(attributeName);
    if (timer === undefined) return;
    clearTimeout(timer);
    this.timers.delete(attributeName);
  }
}

/** How long `ValueChanged` free text waits for typing to pause before refreshing — like the search box. */
export const SPARK_REFRESH_DEBOUNCE_MS = 300;

/** The subset of an attribute definition that decides how its editor behaves. */
export interface RefreshTriggerShape {
  dataType?: string;
  lookupReferenceType?: string;
  isArray?: boolean;
  triggersRefresh?: RefreshTrigger;
}

/** When a change sends its refresh: on the change itself, on blur, or never. */
export type EffectiveRefreshTrigger = 'change' | 'blur' | 'none';

/** How {@link RefreshCoordinator.dispatch} handles a change. */
export type RefreshDispatch = 'immediate' | 'debounced' | 'blur';

/**
 * Resolves the declared `triggersRefresh` against the editor the attribute renders as.
 *
 * - `None` / absent → `'none'`.
 * - `Auto` → `'change'` for a discrete editor, `'blur'` for free text (the original behaviour).
 * - `ValueChanged` → `'change'` (debounced for free text — see {@link refreshDispatch}).
 * - `Blur` → `'blur'`, except on a discrete editor, which has no meaningful blur and so acts as
 *   `ValueChanged`. The server's `--spark-verify-model` warns about that declaration.
 */
export function effectiveTrigger(attr: RefreshTriggerShape | undefined): EffectiveRefreshTrigger {
  switch (attr?.triggersRefresh) {
    case 'Auto':
      return isDiscreteEditor(attr) ? 'change' : 'blur';
    case 'ValueChanged':
      return 'change';
    case 'Blur':
      return isDiscreteEditor(attr) ? 'change' : 'blur';
    default:
      return 'none';
  }
}

/**
 * How a change to `attr` is sent, or `null` when it sends nothing. A `'change'` trigger on free
 * text is debounced; on a discrete editor it is immediate.
 */
export function refreshDispatch(attr: RefreshTriggerShape | undefined): RefreshDispatch | null {
  switch (effectiveTrigger(attr)) {
    case 'change':
      return isDiscreteEditor(attr) ? 'immediate' : 'debounced';
    case 'blur':
      return 'blur';
    default:
      return null;
  }
}

/** Whether the change to `attr` is sent immediately (a discrete editor with a change trigger). */
export function triggersImmediately(attr: RefreshTriggerShape | undefined): boolean {
  return refreshDispatch(attr) === 'immediate';
}

/**
 * Editors where every change is a deliberate, committed one. Everything else is free text, where
 * firing per keystroke would be unacceptable.
 *
 * ⚠️ This asks how the attribute is **rendered**, not what its `dataType` says, and the difference
 * is not academic. A lookup attribute carries the data type of its *key* — Fleet's `Car.Status` is
 * `dataType: "string"` with `lookupReferenceType: "CarStatus"` — so keying on `dataType` alone
 * classifies a `<bs-select>` as free text. It then waits for a blur that a select never emits, and
 * the refresh simply never fires: no request, no error, nothing to see.
 */
export function isDiscreteEditor(attr: RefreshTriggerShape | undefined): boolean {
  if (!attr) return false;

  // A lookup renders as a select (or a modal picker) whatever its key's type is.
  if (attr.lookupReferenceType) return true;

  switch ((attr.dataType ?? '').toLowerCase()) {
    case 'reference':
    case 'lookupreference':
    case 'boolean':
    case 'bool':
    case 'date':
    case 'datetime':
    case 'dateonly':
    case 'enum':
    case 'color':
      return true;
    default:
      return false;
  }
}

/** Empty overlay, so callers do not have to spell the type. */
export const NO_OVERLAY: RefreshOverlay = {};
