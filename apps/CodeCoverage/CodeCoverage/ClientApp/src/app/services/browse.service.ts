import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface CoverageSummary {
  linesCovered: number;
  linesCoverable: number;
  branchesCovered: number;
  branchesTotal: number;
  filesCount: number;
}

export interface RepoInfo {
  /** Repository document id — the /r route forwards to the generic Spark page with it. */
  id: string;
  owner: string;
  name: string;
  fullName: string;
  isPrivate: boolean;
  defaultBranch?: string;
  latestCoverage?: CoverageSummary;
  latestCoverageSha?: string;
  canManage: boolean;
  badgeToken?: string;
  /** The server's public base URL (Coverage:BaseUrl) — badge markdown must use this, not location.origin. */
  baseUrl?: string;
}

/** The package ecosystems the dependency scanner reads manifests for. */
export type DependencyEcosystem = 'npm' | 'nuget' | 'pip' | 'composer' | 'actions' | 'docker';

/** A repository of the account, as a node of its dependency graph. */
export interface DependencyGraphNode {
  /** Repository document id (`Repositories/github/123`). */
  id: string;
  fullName: string;
  name: string;
  isPrivate: boolean;
  archived: boolean;
  scannedAt: string | null;
  scanError: string | null;
}

/** One package a consumer repository takes from a producer repository. */
export interface DependencyGraphPackage {
  ecosystem: DependencyEcosystem;
  name: string;
  manifestPath: string;
  dev: boolean;
}

/** `from` produces the packages that `to` consumes. */
export interface DependencyGraphEdge {
  from: string;
  to: string;
  dependencies: DependencyGraphPackage[];
}

export interface DependencyGraph {
  nodes: DependencyGraphNode[];
  edges: DependencyGraphEdge[];
}

export interface CommitInfo {
  sha: string;
  branch?: string;
  pullRequestNumber?: number;
  message?: string;
  authoredAt?: string;
  coverage?: CoverageSummary;
}

export interface SessionInfo {
  sessionId: string;
  jobName?: string;
  flags: string[];
  parseStatus: string;
  error?: string;
  filesCount: number;
}

export interface BuildInfo {
  // runId, runAttempt and workflowName are deliberately NOT declared here. They are GitHub Actions
  // run identity, the server still sends them, and nothing in this app has ever bound them — so
  // declaring them advertised a shape the UI does not depend on and that a second forge would have
  // had to supply an equivalent for. Re-declare them only alongside a template that reads them.
  status: string;
  finalizeReason?: string;
  createdAtUtc: string;
  coverage?: CoverageSummary;
  sessions: SessionInfo[];
}

/** The commit-level record the headline comes from; null for commits that predate assemblies. */
export interface CommitAssemblyInfo {
  completeness: 'Complete' | 'Partial' | string;
  incompleteReasons: string[];
  measuredFiles: number;
  carriedFiles: number;
  unmeasuredFiles: number;
  baseSha?: string | null;
  baseResolution?: string | null;
  oldestOriginSha?: string | null;
  assembledAtUtc: string;
  builds: string[];
}

export interface CommitDetail extends CommitInfo {
  /** Commit document id — parentId for the generic Spark sub-queries. */
  id: string;
  latestBuildId?: string;
  coverageDeltaVsParent?: number | null;
  coverageDeltaVsDefaultBranch?: number | null;
  assembly?: CommitAssemblyInfo | null;
  /** Per-flag totals of the latest build; keys are sanitized flag names, the same values getTree's flag filter accepts. */
  flagTotals?: Record<string, CoverageSummary> | null;
  builds: BuildInfo[];
}

/** Public account reference; id feeds the generic Spark sub-queries as parentId. */
export interface AccountRef {
  id: string;
  login: string;
}

/** A permission the app declares above the level the account's installation accepted (merge-queue PRD O17). */
export interface PendingPermission {
  name: string;
  displayName: string;
  declared: string;
  /** Null when the installation granted none of it. */
  accepted: string | null;
}

/** The installation's own page on the forge, where the account accepts the new permissions. */
export interface PendingInstallationPermissions {
  htmlUrl: string;
  pending: PendingPermission[];
  /**
   * What the merge queue needs that the app itself does not request (merge-queue PRD §0 D95): `declared` is the
   * level needed, `accepted` what the app requests. No installation can accept these; the app's owner adds them.
   */
  missingFromApp?: PendingPermission[];
  /** Webhook events the merge queue needs that the app is not subscribed to, as GitHub names them (`workflow_run`). */
  missingEvents?: string[];
  /** The app's own "Permissions & events" settings page, where its owner adds what is missing. */
  appSettingsUrl?: string | null;
}

export interface TreeEntry {
  name: string;
  path: string;
  isFile: boolean;
  linesCovered: number;
  linesCoverable: number;
  /** Files only, assembled commits only: 'Measured' on this commit or 'Carried' from carriedFromSha. */
  origin?: 'Measured' | 'Carried' | string | null;
  carriedFromSha?: string | null;
}

export interface TreeResponse {
  buildId: string;
  entries: TreeEntry[];
  /** Sample of unmatched paths (capped server-side); unmatchedTotal is the real count. */
  unmatchedFiles: string[];
  unmatchedTotal: number;
  /**
   * Reports the server could not use, sent only when the tree is empty (#417).
   * A build that measured nothing is an error state, not an empty report —
   * rendering it blank reads as a service outage rather than a fixable upload.
   */
  rejectedReports?: RejectedReport[] | null;
}

export interface RejectedReport {
  fileName: string;
  /** empty | unrecognizedFormat | malformed | truncated | tooLarge | noFiles | missing */
  reason?: string | null;
  detail?: string | null;
}

/** Matches bs-hierarchy-chart's HierarchyNode: id = repo path ('/' for root). */
export interface CoverageHierarchyNode {
  id: string;
  name: string;
  value?: number;
  colorValue?: number;
  children?: CoverageHierarchyNode[];
}

export interface LineCoverageInfo {
  number: number;
  hits?: number;
  status: 'NotCovered' | 'PartiallyCovered' | 'Covered';
}

export interface BranchCoverageInfo {
  line: number;
  covered: number;
  total: number;
}

export interface FileDetail {
  path: string;
  source: string | null;
  lines: LineCoverageInfo[];
  branches: BranchCoverageInfo[];
}

export interface HistoryPoint {
  sha: string;
  timestamp?: string;
  linesCovered: number;
  linesCoverable: number;
  percent: number;
}

/**
 * Why a graph page has no rows, or that it has them (#504):
 * - `ready`: rows follow;
 * - `syncing`: the mirror is being cloned, ask again shortly;
 * - `unavailable`: see `reason`;
 * - `stale`: the repository moved since `snapshot` was taken; reload at the same top SHA (`at=`);
 * - `not-found`: the `at=` SHA is not in the walk.
 */
export type CommitGraphState = 'ready' | 'syncing' | 'unavailable' | 'stale' | 'not-found';

/** A branch (`head`) or tag pointing at a commit. */
export interface CommitGraphRef {
  /** Full ref name, e.g. `refs/heads/main`, `refs/tags/v1.0`. */
  name: string;
  kind: 'head' | 'tag' | string;
  sha: string;
}

/** The coverage overlay of one graph node. */
export interface CommitGraphCoverage {
  /** Null while the commit has a record but no report yet (pending). */
  linesCovered: number | null;
  linesCoverable: number | null;
  /** 0..100; null for 0/0 or pending. */
  percent: number | null;
  /** Percentage points against the parent, when known. */
  deltaVsParent: number | null;
  /** 'Complete' | 'Partial'; null for commits that predate assemblies. */
  completeness: string | null;
}

/** `[column, colour]`. */
export type CommitGraphLane = [number, number];

/** One commit row. Never carries an email: the avatar is fetched by `avatarKey` (D28). */
export interface CommitGraphRow {
  sha: string;
  parents: string[];
  subject: string;
  authorName: string;
  date: string;
  /** The node's column and colour. */
  lane: number;
  colour: number;
  /** Columns whose lines converge into this node from above. */
  up: number[];
  /** One edge per parent leaving the node downwards. */
  down: CommitGraphLane[];
  /** Names of the refs pointing at this commit. */
  refs: string[];
  /** Null when no upload ever reached this commit ("no report"). */
  coverage: CommitGraphCoverage | null;
  /** Feed to `BrowseService.avatarUrl`; null means draw initials. */
  avatarKey: string | null;
}

/** One page of the commit graph (`GET /api/browse/repos/{provider}/{owner}/{name}/graph`). */
export interface CommitGraphPage {
  state: CommitGraphState;
  /** Why `unavailable` (disabled, unsupported, no-access, installation-cap, too-many-refs, too-large, disk-floor, timeout, auth, not-found, error). */
  reason: string | null;
  /** Opaque; send back as `snapshot` on the next page. */
  snapshotId: string | null;
  /** Row count of the whole walk, for the virtual scroll height. */
  totalRows: number;
  from: number;
  /** The widest row on this page; size the graph column per page. */
  laneCount: number;
  /** Lanes active above the first row; pass-throughs are derived from this and the rows. */
  top: CommitGraphLane[];
  /** Every ref on the first page; later pages only those pointing at their rows. */
  refs: CommitGraphRef[];
  rows: CommitGraphRow[];
}

/** Query parameters of the graph endpoint; all optional. */
export interface CommitGraphQuery {
  from?: number;
  /** 1..200, default 100. */
  count?: number;
  /** A full ref name to narrow to one branch and its ancestry; absent = all branches and tags. */
  ref?: string;
  /** A SHA (prefix of at least 7) whose page to return; `from` is then ignored. */
  at?: string;
  /** The `snapshotId` the client already holds. */
  snapshot?: string;
  firstParent?: boolean;
  /** Message search, fixed-string and case-insensitive. */
  grep?: string;
  /** Author search, fixed-string. */
  author?: string;
}

/** A commit whose coverage fell against its parent (`GET …/coverage-drops`, #504 D17). */
export interface CoverageDrop {
  sha: string;
  branch: string | null;
  message: string | null;
  date: string | null;
  /** 0..100; null for 0/0. */
  percent: number | null;
  /** Percentage points against the parent; negative. */
  deltaVsParent: number;
}

/** One pull request in a merge-queue lane (`GET …/merge-queue`, merge-queue PRD M6). */
export interface MergeQueueEntry {
  number: number;
  title: string | null;
  authorLogin: string | null;
  /** The saga's state: `Queued`, `Preparing`, `Regenerating`, `Publishing`, `AwaitingChecks`, `Merging`. */
  state: string;
  /** The last thing the queue did for it, as its check run says. */
  step: string | null;
  sinceUtc: string;
  enqueuedAtUtc: string;
  enqueuedBy: string;
  /** `Label`, `Comment` or `Button` (O8). */
  enqueuedVia: string;
  run: number;
  /** 1 for the entry being worked on, 2.. for the waiting ones. */
  position: number | null;
  /** The regenerate workflow run, while there is one. */
  runUrl: string | null;
}

/** How one run ended: `Merged`, `Evicted`, `Dequeued` or `Refused`, with the queue's reason code. */
export interface MergeQueueOutcome {
  number: number;
  title: string | null;
  run: number;
  state: string;
  reason: string | null;
  message: string | null;
  enqueuedBy: string | null;
  mergeCommitSha: string | null;
  finishedAtUtc: string;
}

export interface MergeQueueLane {
  baseBranch: string;
  paused: boolean;
  current: MergeQueueEntry | null;
  waiting: MergeQueueEntry[];
  /** Newest first. */
  recent: MergeQueueOutcome[];
}

/** The effective configuration (O15) a run read, each value with its source. */
export interface MergeQueueConfig {
  number: number;
  run: number;
  configRef: string | null;
  values: { key: string; value: string; source: string }[];
}

/** Who is looking: the login the button acts as, and the role that decides which buttons make sense (O9 is the engine's). */
export interface MergeQueueViewer {
  signedIn: boolean;
  /** The account has a sign-in with the repository's forge. */
  linked: boolean;
  login: string | null;
  /** `none` … `admin`; null when it could not be read. */
  role: string | null;
  /** `Ok`, `ReauthRequired` or `Unavailable`. */
  credential: string | null;
}

export interface MergeQueueView {
  /** False when the repository's forge has no merge queue. */
  supported: boolean;
  lanes: MergeQueueLane[];
  config: MergeQueueConfig | null;
  viewer: MergeQueueViewer;
}

/** The button's answer: the command was raised as `actor`. The queue itself answers on the pull request. */
export interface MergeQueueCommandAccepted {
  number: number;
  command: 'enqueue' | 'dequeue';
  actor: string;
}

/** One pull request the queue dropdown offers: open, not a draft, on a queued branch, and not in the queue. */
export interface MergeQueueCandidate {
  number: number;
  title: string;
  baseBranch: string;
  authorLogin: string | null;
}

/** The queue dropdown's list, newest first. `unavailable` says why it is empty when it should not be. */
export interface MergeQueueCandidates {
  pullRequests: MergeQueueCandidate[];
  unavailable: string | null;
}

export function coveragePercent(summary?: CoverageSummary | null): number | null {
  if (!summary || summary.linesCoverable === 0) return null;
  return (summary.linesCovered / summary.linesCoverable) * 100;
}

@Injectable({ providedIn: 'root' })
export class BrowseService {
  private readonly http = inject(HttpClient);

  getAccount(provider: string, login: string): Promise<AccountRef> {
    return firstValueFrom(this.http.get<AccountRef>(`/api/browse/accounts/${encodeURIComponent(provider)}/${encodeURIComponent(login)}`));
  }

  getAccountRepos(provider: string, login: string): Promise<RepoInfo[]> {
    return firstValueFrom(this.http.get<RepoInfo[]>(`/api/browse/accounts/${encodeURIComponent(provider)}/${encodeURIComponent(login)}/repos`));
  }

  /**
   * The permissions the app asks for that the account's installation has not accepted yet (merge-queue PRD O17).
   * Null (204) when nothing is pending or nothing can be said; rejects with 401/404 for a viewer who does not
   * manage the account.
   */
  getPendingPermissions(provider: string, login: string): Promise<PendingInstallationPermissions | null> {
    return firstValueFrom(this.http.get<PendingInstallationPermissions | null>(
      `/api/accounts/${encodeURIComponent(provider)}/${encodeURIComponent(login)}/pending-permissions`));
  }

  getDependencyGraph(provider: string, login: string): Promise<DependencyGraph> {
    return firstValueFrom(this.http.get<DependencyGraph>(
      `/api/browse/accounts/${encodeURIComponent(provider)}/${encodeURIComponent(login)}/dependency-graph`));
  }

  getRepo(provider: string, owner: string, name: string): Promise<RepoInfo> {
    return firstValueFrom(this.http.get<RepoInfo>(`/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}`));
  }

  getHistory(provider: string, owner: string, name: string, branch?: string): Promise<HistoryPoint[]> {
    let params = new HttpParams();
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<HistoryPoint[]>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/history`, { params }));
  }

  getSparklines(provider: string, login: string): Promise<Record<string, number[]>> {
    return firstValueFrom(this.http.get<Record<string, number[]>>(
      `/api/browse/accounts/${encodeURIComponent(provider)}/${encodeURIComponent(login)}/sparklines`));
  }

  getBranches(provider: string, owner: string, name: string): Promise<string[]> {
    return firstValueFrom(this.http.get<string[]>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/branches`));
  }

  getCommits(provider: string, owner: string, name: string, branch?: string): Promise<CommitInfo[]> {
    let params = new HttpParams();
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<CommitInfo[]>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits`, { params }));
  }

  getCommit(provider: string, owner: string, name: string, sha: string): Promise<CommitDetail> {
    return firstValueFrom(this.http.get<CommitDetail>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}`));
  }

  getTree(provider: string, owner: string, name: string, sha: string, path?: string, flag?: string): Promise<TreeResponse> {
    let params = new HttpParams();
    if (path) params = params.set('path', path);
    if (flag) params = params.set('flag', flag);
    return firstValueFrom(this.http.get<TreeResponse>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/tree`, { params }));
  }

  getHierarchy(provider: string, owner: string, name: string, sha: string): Promise<CoverageHierarchyNode> {
    return firstValueFrom(this.http.get<CoverageHierarchyNode>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/hierarchy`));
  }

  getFile(provider: string, owner: string, name: string, sha: string, path: string): Promise<FileDetail> {
    const params = new HttpParams().set('path', path);
    return firstValueFrom(this.http.get<FileDetail>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/file`, { params }));
  }

  getCommitGraph(provider: string, owner: string, name: string, query: CommitGraphQuery = {}): Promise<CommitGraphPage> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value === undefined || value === null || value === '' || value === false) continue;
      params = params.set(key, String(value));
    }
    return firstValueFrom(this.http.get<CommitGraphPage>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/graph`, { params }));
  }

  /** The newest commits whose coverage fell by more than `threshold` points; `branch` is a bare branch name. */
  getCoverageDrops(provider: string, owner: string, name: string, threshold: number, branch?: string): Promise<CoverageDrop[]> {
    let params = new HttpParams().set('threshold', String(threshold));
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<CoverageDrop[]>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/coverage-drops`, { params }));
  }

  /** The image URL for a graph row's `avatarKey` (served and cached by the server). */
  avatarUrl(avatarKey: string): string {
    return `/api/browse/avatar/${encodeURIComponent(avatarKey)}`;
  }

  rotateBadgeToken(provider: string, owner: string, name: string): Promise<{ badgeToken: string }> {
    return firstValueFrom(this.http.post<{ badgeToken: string }>(
      `/api/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/settings/badge-token`, {}));
  }

  /** The repository's merge queue: lanes, recent outcomes, the effective configuration and what the viewer may do. */
  getMergeQueue(provider: string, owner: string, name: string): Promise<MergeQueueView> {
    return firstValueFrom(this.http.get<MergeQueueView>(
      `/api/browse/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/merge-queue`));
  }

  /**
   * The repository page's queue button (O8): the same command a label or `/queue` raises, as the signed-in user's own
   * GitHub login. A mutating request, so the XSRF interceptor stamps it.
   */
  enqueuePullRequest(provider: string, owner: string, name: string, number: number): Promise<MergeQueueCommandAccepted> {
    return firstValueFrom(this.http.post<MergeQueueCommandAccepted>(this.mergeQueueUrl(provider, owner, name, number), {}));
  }

  dequeuePullRequest(provider: string, owner: string, name: string, number: number): Promise<MergeQueueCommandAccepted> {
    return firstValueFrom(this.http.delete<MergeQueueCommandAccepted>(this.mergeQueueUrl(provider, owner, name, number)));
  }

  /**
   * The pull requests the queue button may take: open, not drafts, on a branch `coverage.yml` queues, not already queued.
   * The server remembers the forge's answer for half a minute; `refresh` asks the forge again.
   */
  getQueueCandidates(provider: string, owner: string, name: string, refresh = false): Promise<MergeQueueCandidates> {
    return firstValueFrom(this.http.get<MergeQueueCandidates>(
      `/api/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/merge-queue/candidates`,
      refresh ? { params: { refresh: 'true' } } : {}));
  }

  private mergeQueueUrl(provider: string, owner: string, name: string, number: number): string {
    return `/api/repos/${encodeURIComponent(provider)}/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/merge-queue/${encodeURIComponent(String(number))}`;
  }

}
