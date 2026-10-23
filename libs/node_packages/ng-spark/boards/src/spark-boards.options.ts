import { InjectionToken } from '@angular/core';

export interface SparkBoardsOptions {
  /** The board **Pin to dashboard** adds to. Default `home`. */
  pinBoard?: string;
  /** Leave the detail page's **Pin to dashboard** action out. */
  pinAction?: false;
}

export const SPARK_BOARDS_OPTIONS = new InjectionToken<SparkBoardsOptions>('SparkBoardsOptions');
