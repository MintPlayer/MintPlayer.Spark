import { InjectionToken } from '@angular/core';

export interface SparkConfig {
  baseUrl: string;
  /**
   * The concurrency-conflict notice and dialog on the edit page.
   *
   * `showChangedBy`: when true, "Changed by X" names whoever wrote the version merged against, but
   * only when History resolves a display name for exactly that version (a `History/T` holder; one
   * `POST /spark/po/revisions` request). Default false: no History request is made and no user is
   * shown. A raw `ModifiedBy` user id is never shown in either mode. The "at T" time part is shown
   * whenever the object carries `ModifiedAt`.
   */
  conflictDialog?: { showChangedBy?: boolean };
}

export const SPARK_CONFIG = new InjectionToken<SparkConfig>('SPARK_CONFIG');

export const defaultSparkConfig: SparkConfig = {
  baseUrl: '/spark',
  conflictDialog: { showChangedBy: false },
};
