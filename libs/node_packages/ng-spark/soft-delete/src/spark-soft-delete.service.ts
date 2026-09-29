import { inject, Injectable } from '@angular/core';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * Client for the SoftDelete package's endpoints (`MintPlayer.Spark.SoftDelete`, #460 T1). Ids travel
 * in the body — the route table is literal and Raven ids contain slashes.
 */
@Injectable({ providedIn: 'root' })
export class SparkSoftDeleteService {
  private readonly spark = inject(SparkService);

  /**
   * `POST /spark/po/restore` → the restored (live) object. Requires `Restore/T` and a deleted row;
   * anything else is 404 (or 401 anonymous), a restore withheld by `OnDisableActionsAsync` is 403.
   */
  restore(type: string, id: string): Promise<PersistentObject> {
    return this.spark.postEnvelope<PersistentObject>('/po/restore', { objectTypeId: type, id });
  }

  /**
   * `POST /spark/po/purge` → 204. Requires `Purge/T` and a deleted row. Removes the document and
   * every revision of it; it cannot be undone.
   */
  async purge(type: string, id: string): Promise<void> {
    await this.spark.postEnvelope<void>('/po/purge', { objectTypeId: type, id });
  }
}
