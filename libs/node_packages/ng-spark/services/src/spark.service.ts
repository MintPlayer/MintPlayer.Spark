import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { CustomActionDefinition, EntityPermissions, EntityType, LookupReference, LookupReferenceListItem, LookupReferenceValue, PersistentObject, ProgramUnitsConfiguration, QueryResult, SparkQuery, RetryActionPayload, RetryActionResult } from '@mintplayer/ng-spark/models';
import { ClientOperationEnvelope, RetryOperation, SparkClientOperationDispatcher } from '@mintplayer/ng-spark/client-operations';
import { SortColumn } from '@mintplayer/pagination';
import { RetryActionService } from './retry-action.service';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

/**
 * Context for {@link SparkService.newObject}. Everything is optional: a standalone New sends an
 * empty body, and an AsDetail row on a parent that has never been saved sends no `parentId` —
 * which is the honest answer, not a gap. The server hands the hook a null parent rather than
 * trusting the client's copy of one.
 */
export interface NewObjectOptions {
  /** Name of the parent's AsDetail attribute the row is for; absent for a standalone New. */
  asDetailAttribute?: string;
  /** The parent's entity type — required whenever `asDetailAttribute` is set. */
  parentType?: string;
  /** The parent's id, absent while the parent is itself unsaved. */
  parentId?: string;
  /** Free-form arguments, e.g. which variant a New menu chose. */
  parameters?: Record<string, string>;
}

/** Context for {@link SparkService.deleteRow}. Every field is required — see `DeleteRow.cs`. */
export interface DeleteRowOptions {
  asDetailAttribute: string;
  parentType: string;
  parentId: string;
  /** The row's `__sparkRowKey`, as it arrived from the server. */
  rowKey: string;
  parameters?: Record<string, string>;
}

/**
 * The union of every body the envelope helpers send.
 *
 * Named rather than inlined per helper because the New and delete-row calls carry neither a
 * `persistentObject` nor a `triggeredBy`: an inline type that did not list their fields would have
 * rejected them at the call site, and spreading them through `any` would have hidden a typo in a
 * field name the server matches by exact spelling.
 */
type EnvelopeRequestBody = {
  /** The entity type the request is about — the request parameter, not the submitted object's claim. */
  objectTypeId?: string;
  /** The target object's id, for update and delete. */
  id?: string;
  persistentObject?: any;
  triggeredBy?: string;
  retryResults?: RetryActionResult[];
} & Partial<NewObjectOptions> & Partial<Omit<DeleteRowOptions, 'asDetailAttribute' | 'parentType' | 'parentId'>>;

@Injectable({ providedIn: 'root' })
export class SparkService {
  private readonly config = inject(SPARK_CONFIG, { optional: true });
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly http = inject(HttpClient);
  private readonly retryActionService = inject(RetryActionService);

  /**
   * How many prompts one request will answer before giving up. Matches the .NET client's
   * `SparkClient.MaxRetryDepth`, and for the same reason: this is a termination bound, not a
   * capacity one. Far above any real conversation (Fleet's longest is two) and far below anything
   * that could hide a spin.
   */
  private static readonly MAX_RETRY_DEPTH = 16;
  private readonly dispatcher = inject(SparkClientOperationDispatcher);

  // Entity Types
  async getEntityTypes(): Promise<EntityType[]> {
    return firstValueFrom(this.http.get<EntityType[]>(`${this.baseUrl}/types`));
  }

  async getEntityType(id: string): Promise<EntityType> {
    return firstValueFrom(this.http.get<EntityType>(`${this.baseUrl}/types/${encodeURIComponent(id)}`));
  }

  async getEntityTypeByClrType(clrType: string): Promise<EntityType | undefined> {
    const types = await this.getEntityTypes();
    return types.find(t => t.clrType === clrType);
  }

  // Permissions
  async getPermissions(entityTypeId: string): Promise<EntityPermissions> {
    return firstValueFrom(this.http.get<EntityPermissions>(`${this.baseUrl}/permissions/${encodeURIComponent(entityTypeId)}`));
  }

  /** Resolved at most once; see getQueryByName. */
  #queryCatalogue?: Promise<SparkQuery[]>;

  // Queries
  async getQueries(): Promise<SparkQuery[]> {
    return firstValueFrom(this.http.get<SparkQuery[]>(`${this.baseUrl}/queries`));
  }

  async getQuery(id: string): Promise<SparkQuery> {
    return firstValueFrom(this.http.post<SparkQuery>(`${this.baseUrl}/queries/get`, { queryId: id }));
  }

  /**
   * Resolves a query by name, fetching the catalogue at most once per service instance.
   *
   * Without the cache this costs a full `GET /spark/queries` per call, and a form with several
   * reference attributes resolves them in parallel — so opening one page fetched the whole query
   * catalogue N times over. The catalogue is per-caller and effectively static for a page's
   * lifetime; the service is scoped to the app, so a permission change lands on the next load.
   */
  async getQueryByName(name: string): Promise<SparkQuery | undefined> {
    this.#queryCatalogue ??= this.getQueries();
    const queries = await this.#queryCatalogue;
    return queries.find(q => q.name === name);
  }

  async executeQuery(queryId: string, options?: {
    sortColumns?: SortColumn[];
    parentId?: string;
    parentType?: string;
    skip?: number;
    take?: number;
    search?: string;
  }): Promise<QueryResult> {
    // A POST with a typed body, not a GET with a query string. `sortColumns` used to be encoded as
    // `prop:asc,other:desc` — an encoding invented because a query string has no arrays. Column
    // filtering (several columns, several selected values each) would need a second such encoding,
    // which is the reason the reads moved to POST at all.
    return this.sendRead<QueryResult>(`${this.baseUrl}/queries/execute`, {
      queryId,
      sortColumns: options?.sortColumns?.length
        ? options.sortColumns.map(c => ({ property: c.property, direction: c.direction === 'descending' ? 'desc' : 'asc' }))
        : undefined,
      parentId: options?.parentId,
      parentType: options?.parentType,
      skip: options?.skip,
      take: options?.take,
      search: options?.search,
    });
  }

  /**
   * Runs a query identified by name.
   *
   * It used to forward only the parent, dropping skip/take/search — so every reference picker and
   * AsDetail reference column silently saw the server's default of 50 candidates, with no indication
   * that the list was cut. A picker that omits the option you are looking for, and says nothing, is
   * worse than one that fails.
   */
  async executeQueryByName(queryName: string, options?: {
    parentId?: string;
    parentType?: string;
    skip?: number;
    take?: number;
    search?: string;
    sortColumns?: SortColumn[];
  }): Promise<QueryResult> {
    const query = await this.getQueryByName(queryName);
    if (!query) return { columns: [], items: [], totalItems: 0, skip: 0, take: options?.take ?? 50 };
    return this.executeQuery(query.id, options);
  }

  // Program Units
  async getProgramUnits(): Promise<ProgramUnitsConfiguration> {
    return firstValueFrom(this.http.get<ProgramUnitsConfiguration>(`${this.baseUrl}/program-units`));
  }

  // Persistent Objects
  async get(type: string, id: string): Promise<PersistentObject> {
    return this.sendRead<PersistentObject>(`${this.baseUrl}/po/load`, { objectTypeId: type, id });
  }

  async create(type: string, data: Partial<PersistentObject>): Promise<PersistentObject> {
    return this.postWithEnvelope<PersistentObject>(
      `${this.baseUrl}/po/create`,
      { objectTypeId: type, persistentObject: data }
    );
  }

  async update(type: string, id: string, data: Partial<PersistentObject>): Promise<PersistentObject> {
    return this.postWithEnvelope<PersistentObject>(
      `${this.baseUrl}/po/update`,
      { objectTypeId: type, id, persistentObject: data }
    );
  }

  /**
   * Asks the server to reshape an in-progress object after `triggeredBy`'s value changed.
   *
   * Writes nothing, but goes through the envelope like every other mutating call: a refresh may
   * legitimately emit notifications, and may open the retry-action prompt.
   *
   * `triggeredBy` is the attribute's name. For a trigger inside an AsDetail row it is the same
   * path form the inline validation errors use — `Jobs[2].ProfessionId`.
   */
  async refresh(type: string, data: Partial<PersistentObject>, triggeredBy: string): Promise<PersistentObject> {
    return this.postWithEnvelope<PersistentObject>(
      `${this.baseUrl}/po/refresh`,
      { objectTypeId: type, persistentObject: data, triggeredBy }
    );
  }

  /**
   * Asks the server to construct a new object of `type`, so `OnNewAsync` can default it.
   *
   * Writes nothing — the object comes back unsaved, and for an AsDetail row the parent still owns
   * the save. Only called for a row type whose `serverSideRowLifecycle` is on; every other type
   * keeps building its blank row locally, which is why switching the flag off costs no request.
   *
   * ⚠️ The row comes back **keyed**: the server constructs the CLR instance, so the row-key field
   * initializer runs and `po.id` carries the key. Flattening it with `nestedPoToDict` therefore
   * populates `__sparkRowKey`, and the row is matchable on save from the moment it is added — the
   * whole reason this round-trip is worth a request.
   */
  async newObject(type: string, options?: NewObjectOptions): Promise<PersistentObject> {
    return this.postWithEnvelope<PersistentObject>(
      `${this.baseUrl}/po/new`,
      { objectTypeId: type, ...(options ?? {}) }
    );
  }

  /**
   * Asks whether a stored row may leave its parent's AsDetail collection.
   *
   * Deletes nothing: a resolved promise means the caller may splice the row out of the collection
   * it is editing, and the removal is persisted with the parent. A hook that refuses rejects with a
   * 400 whose `error.error.result.errors` carries the readable reason, exactly like a save.
   *
   * A POST — as everything is now. It used to be worth explaining: the framework's real delete route
   * was a catch-all that would have swallowed a sibling DELETE. The catch-all is gone with the rest
   * of the route variables.
   */
  async deleteRow(type: string, options: DeleteRowOptions): Promise<void> {
    await this.postWithEnvelope<{ removed: boolean }>(
      `${this.baseUrl}/po/delete-row`,
      { objectTypeId: type, ...options }
    );
  }

  async delete(type: string, id: string): Promise<void> {
    return this.postWithEnvelope<void>(
      `${this.baseUrl}/po/delete`,
      { objectTypeId: type, id }
    );
  }

  // Custom Actions
  async getCustomActions(objectTypeId: string): Promise<CustomActionDefinition[]> {
    return firstValueFrom(this.http.post<CustomActionDefinition[]>(`${this.baseUrl}/actions/list`, { objectTypeId }));
  }

  /**
   * @param parent The object of THIS type the action is operating on — the detail-page invocation.
   * @param selectedItemIds Row ids from a query. Ids, never row objects: a row is a projection.
   * @param queryParent When invoked from a sub-query, the object whose detail page it was rendered
   *   on — a DIFFERENT type (the cars listed on a company's page are Cars, the page is a Company).
   *   Sent as id + type, exactly as the query endpoint names it, and resolved server-side under its
   *   own type with its own Read gate.
   * @param queryId The query the selection came from, so the server can re-run it narrowed to those
   *   ids and hand the action the rows the grid actually had -- index-computed columns included.
   */
  async executeCustomAction(
    objectTypeId: string,
    actionName: string,
    parent?: PersistentObject,
    selectedItemIds?: string[],
    queryParent?: { id: string; type: string },
    queryId?: string,
  ): Promise<void> {
    const body: {
      objectTypeId: string; actionName: string;
      parent?: PersistentObject; selectedItemIds?: string[];
      parentId?: string; parentType?: string; queryId?: string;
      retryResults?: RetryActionResult[];
    } = { objectTypeId, actionName, parent, selectedItemIds, parentId: queryParent?.id, parentType: queryParent?.type, queryId };
    return this.postWithEnvelope<void>(
      `${this.baseUrl}/actions/execute`,
      body as any
    );
  }

  // LookupReferences
  async getLookupReferences(): Promise<LookupReferenceListItem[]> {
    return firstValueFrom(this.http.get<LookupReferenceListItem[]>(`${this.baseUrl}/lookupref`));
  }

  async getLookupReference(name: string): Promise<LookupReference> {
    return firstValueFrom(this.http.get<LookupReference>(`${this.baseUrl}/lookupref/${encodeURIComponent(name)}`));
  }

  async addLookupReferenceValue(name: string, value: LookupReferenceValue): Promise<LookupReferenceValue> {
    return firstValueFrom(this.http.post<LookupReferenceValue>(`${this.baseUrl}/lookupref/${encodeURIComponent(name)}`, value));
  }

  async updateLookupReferenceValue(name: string, key: string, value: LookupReferenceValue): Promise<LookupReferenceValue> {
    return firstValueFrom(this.http.put<LookupReferenceValue>(
      `${this.baseUrl}/lookupref/${encodeURIComponent(name)}/${encodeURIComponent(key)}`,
      value
    ));
  }

  async deleteLookupReferenceValue(name: string, key: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(
      `${this.baseUrl}/lookupref/${encodeURIComponent(name)}/${encodeURIComponent(key)}`
    ));
  }

  // Envelope-aware HTTP helpers.
  // All mutation endpoints (Create / Update / Delete / Execute custom action) emit the
  // ClientOperationEnvelope { result, operations } shape. These helpers unwrap the envelope,
  // dispatch any non-retry operations, and translate 449 retry-operations into the existing
  // RetryActionService modal flow.

  private postWithEnvelope<T>(url: string, body: EnvelopeRequestBody): Promise<T> {
    return this.sendWithEnvelope<T>(
      () => firstValueFrom(this.http.post<ClientOperationEnvelope<T>>(url, body)),
      body,
      () => this.postWithEnvelope<T>(url, body),
    );
  }

  /**
   * A read: posts the request, and returns the response **as-is** rather than unwrapping an
   * envelope.
   *
   * The read endpoints moved from GET to POST so their hooks could prompt, but their success
   * responses are still bare — a `PersistentObject`, a `QueryResult`. Only the 449 is enveloped,
   * because a retry has nowhere else to live. So this shares the retry loop with the mutating calls
   * and nothing else.
   */
  private async sendRead<T>(url: string, body: Record<string, unknown>): Promise<T> {
    try {
      return await firstValueFrom(this.http.post<T>(url, body));
    } catch (error) {
      return this.handleEnvelopeRetryError<T>(
        error as HttpErrorResponse,
        () => this.sendRead<T>(url, body),
        body as { retryResults?: RetryActionResult[] },
      );
    }
  }

  private async sendWithEnvelope<T>(
    send: () => Promise<ClientOperationEnvelope<T>>,
    body: { retryResults?: RetryActionResult[] },
    retryFn: () => Promise<T>,
  ): Promise<T> {
    try {
      const envelope = await send();
      if (envelope?.operations?.length) {
        this.dispatcher.dispatch(envelope.operations);
      }
      return envelope?.result as T;
    } catch (error) {
      return this.handleEnvelopeRetryError<T>(error as HttpErrorResponse, retryFn, body);
    }
  }

  private async handleEnvelopeRetryError<T>(
    error: HttpErrorResponse,
    retryFn: () => Promise<T>,
    body: { retryResults?: RetryActionResult[] }
  ): Promise<T> {
    if (error.status !== 449) throw error;
    const envelope = error.error as ClientOperationEnvelope<T> | undefined;
    if (!envelope?.operations?.length) throw error;

    // Dispatch any non-retry operations accumulated before the retry throw
    // so notify/refresh/etc. fire BEFORE the retry modal opens.
    const nonRetry = envelope.operations.filter(o => o.type !== 'retry');
    if (nonRetry.length) this.dispatcher.dispatch(nonRetry);

    const retryOp = envelope.operations.find(o => o.type === 'retry') as RetryOperation | undefined;
    if (!retryOp) throw error;

    // ⚠️ A hook that raises a retry WITHOUT first checking `Retry.Result` re-raises on every
    // resubmission, and this loop has no natural end: the modal reopens, the user answers, the
    // server asks again. Each round trip looks individually reasonable, so the tab spins until it is
    // closed and nothing is logged. The trap is documented in RetryFromEveryHookTests, and the .NET
    // client has had a bound since M3; this is the browser's.
    //
    // The bound is on ANSWERS ALREADY GIVEN, which is what `retryResults` is — so it counts the
    // conversation and not the component's lifetime, and two unrelated conversations do not add up.
    if ((body.retryResults?.length ?? 0) >= SparkService.MAX_RETRY_DEPTH) {
      throw new Error(
        `Gave up after answering ${SparkService.MAX_RETRY_DEPTH} retry prompts; the last was step ` +
        `${retryOp.step} ("${retryOp.title}"). A hook that raises a retry without checking ` +
        `Retry.Result first will do this — it re-raises on every resubmission.`);
    }

    const payload: RetryActionPayload = {
      type: 'retry-action',
      step: retryOp.step,
      title: retryOp.title,
      options: retryOp.options,
      defaultOption: retryOp.defaultOption ?? undefined,
      persistentObject: retryOp.persistentObject ?? undefined,
      message: retryOp.message ?? undefined,
    };
    const result = await this.retryActionService.show(payload);
    if (result.option === 'Cancel' && !payload.options.includes('Cancel')) throw error;

    body.retryResults = [...(body.retryResults || []), result];
    return retryFn();
  }
}
