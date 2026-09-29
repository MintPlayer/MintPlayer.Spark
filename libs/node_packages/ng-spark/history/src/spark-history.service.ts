import { inject, Injectable } from '@angular/core';
import { PersistentObject, SparkDeletedFilter } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';

/** One revision of a row, newest first (`MintPlayer.Spark.History.SparkRevision`). */
export interface SparkRevision {
  changeVector: string;
  /** When the revision was written (ISO 8601 with offset); null when the server does not know. */
  lastModified?: string | null;
  /** Who wrote it — an id (D8: audit fields hold ids only). */
  userId?: string | null;
  /** The id resolved to a name at read time, when the app registered an `IHistoryUserNameResolver`. */
  userName?: string | null;
  /** The revision RavenDB writes when the document is deleted (a purge-free hard delete). */
  isDeleteRevision?: boolean;
  /** The revision the document currently is. Reverting to it changes nothing. */
  isCurrent?: boolean;
}

export interface SparkRevisionListOptions {
  skip?: number;
  /** Server default 50, at most 200. */
  take?: number;
  /** `include` / `only` to read the history of a soft-deleted row (`ViewDeleted` holders only). */
  deleted?: SparkDeletedFilter | null;
}

/**
 * Client for the History package's endpoints (`MintPlayer.Spark.History`, #460 T1). Every read is
 * gated on the CURRENT row (a row the caller can no longer see has no readable history) and on
 * `History/T`; old revisions come back redacted like the live object.
 */
@Injectable({ providedIn: 'root' })
export class SparkHistoryService {
  private readonly spark = inject(SparkService);

  /** `POST /spark/po/revisions` → the row's revisions, newest first. */
  list(type: string, id: string, options?: SparkRevisionListOptions): Promise<SparkRevision[]> {
    return this.spark.postEnvelope<SparkRevision[]>('/po/revisions', {
      objectTypeId: type,
      id,
      ...(options?.skip ? { skip: options.skip } : {}),
      ...(options?.take ? { take: options.take } : {}),
      ...(options?.deleted && options.deleted !== 'exclude' ? { deleted: options.deleted } : {}),
    });
  }

  /** `POST /spark/po/revision` → the revision as a read-only, redacted persistent object. */
  get(type: string, id: string, changeVector: string, options?: { deleted?: SparkDeletedFilter | null }): Promise<PersistentObject> {
    return this.spark.postEnvelope<PersistentObject>('/po/revision', {
      objectTypeId: type,
      id,
      changeVector,
      ...(options?.deleted && options.deleted !== 'exclude' ? { deleted: options.deleted } : {}),
    });
  }

  /**
   * `POST /spark/po/revert` → the reverted row. Requires `History/T`, `Revert/T` and `Edit/T`; saved
   * through the normal pipeline. 403 = the revert is disabled for this row, 409 = the row changed
   * since it was loaded, 400 = an interceptor refused (e.g. the row is locked).
   */
  revert(type: string, id: string, changeVector: string): Promise<PersistentObject> {
    return this.spark.postEnvelope<PersistentObject>('/po/revert', { objectTypeId: type, id, changeVector });
  }
}
