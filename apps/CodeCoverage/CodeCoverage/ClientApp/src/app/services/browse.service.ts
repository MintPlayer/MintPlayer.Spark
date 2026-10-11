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

}
