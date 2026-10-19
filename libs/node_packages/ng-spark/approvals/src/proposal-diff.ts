import { EntityType, PersistentObject, SparkRevisionChange, diffRevision, revisionAttributes } from '@mintplayer/ng-spark/models';
import { SparkApprovalProposal } from './spark-approvals.models';

/**
 * The attributes a proposal's diff shows (commerce #521, D9): for a modification the shown attributes
 * the proposal changes, current → proposed; for a creation — or a modification of a row the caller may
 * not read — every shown attribute the proposal sets, with nothing before.
 *
 * Only attributes the proposed object carries are compared: a save sends what the form held, and an
 * attribute it left out is not a proposal to clear it.
 */
export function proposalChanges(entityType: EntityType | null | undefined, proposal: SparkApprovalProposal | null | undefined): SparkRevisionChange[] {
  if (!proposal?.proposed) return [];
  const proposed: PersistentObject = { ...proposal.proposed, attributes: proposal.proposed.attributes ?? [] };
  if (proposal.operation === 'Modify' && proposal.current) {
    return diffRevision(entityType, proposal.current, proposed).filter(change => change.current !== undefined);
  }
  return revisionAttributes(entityType)
    .map(attribute => ({ attribute, revision: undefined, current: proposed.attributes.find(a => a.name === attribute.name) }))
    .filter(change => change.current !== undefined);
}
