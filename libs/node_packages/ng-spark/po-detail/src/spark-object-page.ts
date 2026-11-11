import { DestroyRef, PendingTasks, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { combineLatest } from 'rxjs';
import { SparkObjectUrlService, SparkSeoService, SparkService } from '@mintplayer/ng-spark/services';
import { EntityType, PersistentObject, SparkDeletedFilter, sparkDetailLink } from '@mintplayer/ng-spark/models';
import { parseSparkDeletedParam } from '@mintplayer/ng-spark/panels';

/** How a page that shows one object takes part in {@link sparkObjectPage}. */
export interface SparkObjectPageOptions {
  /**
   * True while a host embeds the object (a board tile): the route is not followed, and the address bar and the
   * SEO tags are left alone. Read on every route change and load, so it may be backed by an input.
   */
  embedded?: () => boolean;

  /**
   * Reads that must be answered before the object is published, such as its rights and actions. A page is reused
   * when the route moves to another object, so publishing first would show the new object under the previous one's
   * buttons until they arrive. The returned callback runs right after the object is published, and not at all when
   * a move to another object overtook the load.
   */
  beforePublish?: (entityType: EntityType | null, item: PersistentObject) => Promise<(() => void) | void>;

  /** Runs when the page moves to another object, before it loads (the previous object's pushes must stop). */
  onTargetChange?: () => void;
}

/**
 * The lifecycle of a page that shows one persistent object (#601, D19): which object (the route's `:type`, or a
 * type's own route's `data.sparkType`, and `:id`), its load, and its URL and SEO.
 *
 * - The object, its type and the whole catalogue load together; a load overtaken by a move to another object
 *   publishes nothing.
 * - The server render waits for the load (`PendingTasks`).
 * - Once published, the address bar gets the object's canonical `href` (#602; slug and `?lang` included), and the
 *   page its title, canonical, hreflang and Open Graph tags. A deleted view and a refused or missing object are
 *   never indexed, and the tags are reset when the page goes away.
 *
 * `spark-po-detail` is built on it, and so is any custom page for one type (tier (a) of the slugs PRD):
 *
 * ```ts
 * protected readonly page = sparkObjectPage();
 * // template: page.item(), page.entityType(), page.errorMessage()
 * ```
 *
 * Call it in an injection context (a field initializer or the constructor).
 */
export function sparkObjectPage(options: SparkObjectPageOptions = {}): SparkObjectPage {
  return new SparkObjectPage(options);
}

export class SparkObjectPage {
  private readonly route = inject(ActivatedRoute);
  private readonly sparkService = inject(SparkService);
  private readonly pendingTasks = inject(PendingTasks);
  private readonly objectUrl = inject(SparkObjectUrlService);
  private readonly seo = inject(SparkSeoService);

  /** The object on screen; null until loaded, and after a failed load. */
  readonly item = signal<PersistentObject | null>(null);
  /** Its type, from the catalogue; null when the catalogue does not list it. */
  readonly entityType = signal<EntityType | null>(null);
  /** The whole catalogue, for reference links and AsDetail row types. */
  readonly allEntityTypes = signal<EntityType[]>([]);
  /** Why the object could not be loaded: shown instead of the page. */
  readonly errorMessage = signal<string | null>(null);
  /**
   * The route's `?deleted=` mode (#460, T2): `only` when the row was opened from the recycle bin. Sent with every
   * load; the server honours it only for `ViewDeleted` holders, so for anyone else a deleted row stays a 404.
   */
  readonly deletedMode = signal<SparkDeletedFilter | null>(null);

  /** The type (id or alias) and the id of the object on screen. */
  type = '';
  id = '';

  constructor(private readonly options: SparkObjectPageOptions) {
    // The query map too: Restore navigates from `?deleted=only` to the same path without it, and the object must
    // then be re-read as a live row. The first emission is synchronous, before a host's inputs are set, so an
    // embedded object is told apart by the route having no type (a board's route).
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        // `:type` on the generic routes; a type's own route (sparkObjectRoute, #602) names it in its data.
        const type = params.get('type') ?? this.route.snapshot.data['sparkType'] ?? null;
        if (!type || this.embedded()) return;
        void this.open(type, params.get('id') || '', query.get('deleted'));
      });

    inject(DestroyRef).onDestroy(() => {
      if (!this.embedded()) this.seo.reset();
    });
  }

  private embedded(): boolean {
    return this.options.embedded?.() ?? false;
  }

  /** Moves the page to another object and loads it. */
  async open(type: string, id: string, deleted: string | null): Promise<void> {
    this.type = type;
    this.id = id;
    this.options.onTargetChange?.();
    const mode = parseSparkDeletedParam(deleted);
    // `exclude` is the default on the wire; keep it out of the request like an absent parameter.
    this.deletedMode.set(mode === 'exclude' ? null : mode);
    await this.load();
  }

  /** The object as the page's current mode sees it. Only a set mode is sent. */
  loadItem(): Promise<PersistentObject> {
    const deleted = this.deletedMode();
    return deleted
      ? this.sparkService.get(this.type, this.id, { deleted })
      : this.sparkService.get(this.type, this.id);
  }

  /** (Re)loads the object and its type. */
  async load(): Promise<void> {
    this.errorMessage.set(null);
    // A load overtaken by a move to another object publishes nothing.
    const type = this.type, id = this.id;
    const stale = () => type !== this.type || id !== this.id;
    // The app is not stable until the load is done. Angular turns its transfer cache off at the first stable moment
    // after hydration (docs/spark_ssr_PRD.md SP3). Publishing schedules a change detection, which holds the app
    // unstable until it ran, so the attribute presenter's own reads start before this task ends.
    const done = this.pendingTasks.add();
    try {
      const [entityTypes, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.loadItem(),
      ]);
      const et = entityTypes.find(t => t.id === type || t.alias === type) || null;
      const published = await this.options.beforePublish?.(et, item);
      if (stale()) return;

      this.allEntityTypes.set(entityTypes);
      this.entityType.set(et);
      this.item.set(item);
      this.writeUrl(item);
      this.writeSeo(et, item);
      published?.();
    } catch (e) {
      if (stale()) return;
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
      if (!this.embedded()) this.seo.noIndex();
    } finally {
      done();
    }
  }

  /**
   * Puts the object's canonical page (`href`, #602) in the address bar, or corrects a stale slug, without a
   * navigation. Not for an embedded object.
   */
  private writeUrl(item: PersistentObject): void {
    if (this.embedded()) return;
    this.objectUrl.apply(item);
  }

  /** The title and SEO tags; a deleted view of the object is never indexed. */
  private writeSeo(et: EntityType | null, item: PersistentObject): void {
    if (this.embedded()) return;
    if (this.deletedMode()) {
      this.seo.noIndex();
      return;
    }
    this.seo.forObject(et, item, item.href || sparkDetailLink(et ?? this.type, this.id));
  }
}
