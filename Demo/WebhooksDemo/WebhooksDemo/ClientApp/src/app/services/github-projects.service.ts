import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { GitHubProjectInfo, ProjectColumn } from '../models/github-project';

@Injectable({ providedIn: 'root' })
export class GitHubProjectsService {
  private readonly http = inject(HttpClient);

  listProjects(): Promise<GitHubProjectInfo[]> {
    return firstValueFrom(this.http.get<GitHubProjectInfo[]>('/api/github/projects'));
  }

  getColumns(nodeId: string, installationId: number): Promise<{ statusFieldId: string; columns: ProjectColumn[] }> {
    return firstValueFrom(
      this.http.get<{ statusFieldId: string; columns: ProjectColumn[] }>(
        `/api/github/projects/${encodeURIComponent(nodeId)}/columns`,
        { params: new HttpParams().set('installationId', installationId) }
      )
    );
  }

  syncColumns(documentId: string): Promise<{ statusFieldId: string; columns: ProjectColumn[] }> {
    return firstValueFrom(
      this.http.post<{ statusFieldId: string; columns: ProjectColumn[] }>(
        `/api/github/projects/${encodeURIComponent(documentId)}/sync-columns`, {}
      )
    );
  }
}
