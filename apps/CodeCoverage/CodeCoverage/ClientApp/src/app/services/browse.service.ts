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
  runId: number;
  runAttempt: number;
  status: string;
  finalizeReason?: string;
  workflowName?: string;
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
  blockId: string;
  branchId: string;
  taken?: number;
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

export function coveragePercent(summary?: CoverageSummary | null): number | null {
  if (!summary || summary.linesCoverable === 0) return null;
  return (summary.linesCovered / summary.linesCoverable) * 100;
}

@Injectable({ providedIn: 'root' })
export class BrowseService {
  private readonly http = inject(HttpClient);

  getAccount(login: string): Promise<AccountRef> {
    return firstValueFrom(this.http.get<AccountRef>(`/api/browse/accounts/${encodeURIComponent(login)}`));
  }

  getAccountRepos(login: string): Promise<RepoInfo[]> {
    return firstValueFrom(this.http.get<RepoInfo[]>(`/api/browse/accounts/${encodeURIComponent(login)}/repos`));
  }

  getRepo(owner: string, name: string): Promise<RepoInfo> {
    return firstValueFrom(this.http.get<RepoInfo>(`/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}`));
  }

  getHistory(owner: string, name: string, branch?: string): Promise<HistoryPoint[]> {
    let params = new HttpParams();
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<HistoryPoint[]>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/history`, { params }));
  }

  getSparklines(login: string): Promise<Record<string, number[]>> {
    return firstValueFrom(this.http.get<Record<string, number[]>>(
      `/api/browse/accounts/${encodeURIComponent(login)}/sparklines`));
  }

  getBranches(owner: string, name: string): Promise<string[]> {
    return firstValueFrom(this.http.get<string[]>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/branches`));
  }

  getCommits(owner: string, name: string, branch?: string): Promise<CommitInfo[]> {
    let params = new HttpParams();
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<CommitInfo[]>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits`, { params }));
  }

  getCommit(owner: string, name: string, sha: string): Promise<CommitDetail> {
    return firstValueFrom(this.http.get<CommitDetail>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}`));
  }

  getTree(owner: string, name: string, sha: string, path?: string, flag?: string): Promise<TreeResponse> {
    let params = new HttpParams();
    if (path) params = params.set('path', path);
    if (flag) params = params.set('flag', flag);
    return firstValueFrom(this.http.get<TreeResponse>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/tree`, { params }));
  }

  getHierarchy(owner: string, name: string, sha: string): Promise<CoverageHierarchyNode> {
    return firstValueFrom(this.http.get<CoverageHierarchyNode>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/hierarchy`));
  }

  getFile(owner: string, name: string, sha: string, path: string): Promise<FileDetail> {
    const params = new HttpParams().set('path', path);
    return firstValueFrom(this.http.get<FileDetail>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/file`, { params }));
  }

  rotateBadgeToken(owner: string, name: string): Promise<{ badgeToken: string }> {
    return firstValueFrom(this.http.post<{ badgeToken: string }>(
      `/api/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/settings/badge-token`, {}));
  }

  getGate(owner: string, name: string): Promise<GateSettings> {
    return firstValueFrom(this.http.get<GateSettings>(
      `/api/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/settings/gate`));
  }

  putGate(owner: string, name: string, gate: GateSettings): Promise<GateSettings> {
    return firstValueFrom(this.http.put<GateSettings>(
      `/api/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/settings/gate`, gate));
  }
}

/** Mirror of the server's GateSettings; the GET spells every default out. */
export interface GateSettings {
  projectMode: 'auto' | 'fixed';
  projectTarget?: number | null;
  projectThreshold: number;
  projectBasis: 'scoped' | 'projection';
  patchTarget?: number | null;
  patchThreshold: number;
  blocking: boolean;
}
