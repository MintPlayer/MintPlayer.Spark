// The revision diff moved to models (commerce #521, M3) so Approvals' proposal diff shares it without
// depending on History; re-exported here so History's public surface is unchanged.
export { comparableValue, diffRevision, revisionAttributes } from '@mintplayer/ng-spark/models';
export type { SparkRevisionChange } from '@mintplayer/ng-spark/models';
