import { PersistentObject, RefreshOverlay } from '@mintplayer/ng-spark/models';

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

  /** Sends a pending refresh for `attributeName`, if one was marked. */
  blur(attributeName: string): Promise<void> {
    if (!this.pending.delete(attributeName)) return Promise.resolve();
    return this.trigger(attributeName);
  }

  /**
   * Sends every refresh still marked pending. Called before save, so a value typed and never blurred
   * — the user tabbing straight to the save button — is still reflected before the object goes.
   */
  async flush(): Promise<void> {
    const names = [...this.pending];
    this.pending.clear();
    for (const name of names) await this.trigger(name);
    await this.queue;
  }

  /** Sends a refresh immediately — discrete editors, where every change is a committed one. */
  trigger(attributeName: string): Promise<void> {
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
}

/** The subset of an attribute definition that decides how its editor behaves. */
export interface RefreshTriggerShape {
  dataType?: string;
  lookupReferenceType?: string;
  isArray?: boolean;
}

/**
 * Editors where every change is a deliberate, committed one, so a refresh fires immediately.
 * Everything else is free text, where firing per keystroke would be unacceptable and the refresh
 * waits for blur.
 *
 * ⚠️ This asks how the attribute is **rendered**, not what its `dataType` says, and the difference
 * is not academic. A lookup attribute carries the data type of its *key* — Fleet's `Car.Status` is
 * `dataType: "string"` with `lookupReferenceType: "CarStatus"` — so keying on `dataType` alone
 * classifies a `<bs-select>` as free text. It then waits for a blur that a select never emits, and
 * the refresh simply never fires: no request, no error, nothing to see.
 */
export function triggersImmediately(attr: RefreshTriggerShape | undefined): boolean {
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
