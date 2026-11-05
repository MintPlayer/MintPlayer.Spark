import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import {
  SPARK_DETAIL_ACTIONS,
  SPARK_DETAIL_LAYOUTS,
  SPARK_DETAIL_PANELS,
  SPARK_QUERY_LIST_ACTIONS,
  SPARK_QUERY_ROW_ACTIONS,
  SparkDetailAction,
  SparkDetailLayout,
  SparkDetailPanel,
  SparkQueryListAction,
  SparkQueryRowAction,
} from './spark-panels';

/** Grid row-menu actions, for `provideSpark(...)` (every listing adds). */
export function withQueryRowActions(...actions: SparkQueryRowAction[]): SparkFeature {
  return sparkFeature('queryRowActions', SparkFeatureOrder.registrations,
    actions.map(action => ({ provide: SPARK_QUERY_ROW_ACTIONS, useValue: action, multi: true })), { multi: true });
}

/** Detail layouts, for `provideSpark(...)` (every listing adds). */
export function withDetailLayouts(...layouts: SparkDetailLayout[]): SparkFeature {
  return sparkFeature('detailLayouts', SparkFeatureOrder.registrations,
    layouts.map(layout => ({ provide: SPARK_DETAIL_LAYOUTS, useValue: layout, multi: true })), { multi: true });
}

/** Detail panels, for `provideSpark(...)` (every listing adds). */
export function withDetailPanels(...panels: SparkDetailPanel[]): SparkFeature {
  return sparkFeature('detailPanels', SparkFeatureOrder.registrations,
    panels.map(panel => ({ provide: SPARK_DETAIL_PANELS, useValue: panel, multi: true })), { multi: true });
}

/** Detail-page actions, for `provideSpark(...)` (every listing adds). */
export function withDetailActions(...actions: SparkDetailAction[]): SparkFeature {
  return sparkFeature('detailActions', SparkFeatureOrder.registrations,
    actions.map(action => ({ provide: SPARK_DETAIL_ACTIONS, useValue: action, multi: true })), { multi: true });
}

/** Query-page actions, for `provideSpark(...)` (every listing adds). */
export function withQueryListActions(...actions: SparkQueryListAction[]): SparkFeature {
  return sparkFeature('queryListActions', SparkFeatureOrder.registrations,
    actions.map(action => ({ provide: SPARK_QUERY_LIST_ACTIONS, useValue: action, multi: true })), { multi: true });
}
