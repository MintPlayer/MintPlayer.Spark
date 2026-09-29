/** A target's score and the caller's vote (`/spark/moderation/vote`, `/votes`). */
export interface ModerationVoteState {
  id: string;
  score: number;
  up: number;
  down: number;
  /** +1, −1 or 0. */
  myVote: -1 | 0 | 1;
  locked: boolean;
  /**
   * Whether the caller holds the right to upvote / downvote this type (an earned privilege or a
   * group grant). Not a promise the vote is accepted: an own post, a lock or a suspension still
   * refuse it. Absent from an older server, which the widget reads as "may".
   */
  canUpvote?: boolean;
  canDownvote?: boolean;
}

/** A user's reputation. Someone else's carries only `userId` and `total`. */
export interface ModerationReputation {
  userId: string;
  total: number;
  pending: number;
  privileges: string[];
  suspended: boolean;
}

export interface ModerationReputationLine {
  label: string;
  kind: string;
  points: number;
  targetId?: string | null;
  targetType?: string | null;
  atUtc: string;
  pending: boolean;
}

/** `/spark/moderation/status` — the moderator panel's view of one target. */
export interface ModerationStatus {
  id: string;
  authorId?: string | null;
  locked: boolean;
  lockReason?: string | null;
  canLock: boolean;
  canReview: boolean;
  canSuspend: boolean;
  openCaseId?: string | null;
  openFlags: number;
}

export interface ModerationCaseSummary {
  id: string;
  /** `flag`, or the fraud rule (`serial`, `concentration`, `reciprocal`, `fast-voting`, `registration-cluster`). */
  kind: string;
  status: string;
  targetId?: string | null;
  targetType?: string | null;
  accountIds: string[];
  flagCount: number;
  voteCount: number;
  summary?: string | null;
  openedAtUtc: string;
  decision?: string | null;
}

export interface ModerationCaseDetail {
  case: ModerationCaseSummary;
  matrix: { voterId: string; authorId: string; votes: number }[];
  timeline: {
    voteId: string;
    voterId: string;
    authorId?: string | null;
    targetId: string;
    direction: number;
    castAtUtc: string;
    targetPostedAtUtc?: string | null;
    secondsAfterPost?: number | null;
  }[];
  accounts: {
    id: string;
    createdAtUtc?: string | null;
    ageDays?: number | null;
    registrationMethod?: string | null;
    /** How many other accounts share a network hash with this one — never an address. */
    sharesNetworkWith: number;
    reputationBefore: number;
    reputationAfter: number;
  }[];
  flags: { flaggerId: string; reason: string; raisedAtUtc: string; status: string }[];
}

export interface ModerationAuditEntry {
  id: string;
  actorId?: string | null;
  action: string;
  targetId?: string | null;
  targetType?: string | null;
  subjectUserId?: string | null;
  caseId?: string | null;
  reason?: string | null;
  details?: string | null;
  atUtc: string;
}
