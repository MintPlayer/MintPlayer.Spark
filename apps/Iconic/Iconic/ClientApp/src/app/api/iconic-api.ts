import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { PatchOp } from '../editor/core/diff';
import type { Scene } from '../editor/core/model';

/** The editor's server API, /api/iconic (Iconic/Endpoints/IconicEndpoints.cs). The XSRF header comes from withSparkAuth(). */

export type ProjectKind = 'Icon' | 'Cursor' | 'Image' | 'Animation';
export const PROJECT_KINDS: readonly ProjectKind[] = ['Icon', 'Cursor', 'Image', 'Animation'];

export interface Usage {
  sceneBytes: number;
  maxSceneBytes: number;
  attachmentBytes: number;
  maxAttachmentBytes: number;
  snapshots: number;
  maxSnapshots: number;
  maxProjects: number;
}

export interface SnapshotInfo { id: string; name: string; createdAt: string; bytes: number }
export interface FileInfo { name: string; size: number }

export interface ProjectDto {
  key: string;
  name: string;
  kind: ProjectKind;
  width: number;
  height: number;
  isPublic: boolean;
  shareToken: string | null;
  shareTakenDown: boolean;
  version: number;
  scene: unknown;
  snapshots: SnapshotInfo[];
  files: FileInfo[];
  usage: Usage;
}

export interface ProjectListItem {
  key: string;
  name: string;
  kind: ProjectKind;
  width: number;
  height: number;
  modifiedAt: string | null;
  isPublic: boolean;
  hasThumbnail: boolean;
}

export interface SharedDto {
  name: string;
  kind: ProjectKind;
  width: number;
  height: number;
  scene: unknown;
  files: FileInfo[];
}

/** The server's error body: `{ code, message }`, plus `version` on a 409 version conflict. */
export interface ApiError {
  status: number;
  code: string;
  message: string;
  version?: number;
}

export function apiError(e: unknown): ApiError {
  if (e instanceof HttpErrorResponse) {
    const body = (e.error && typeof e.error === 'object' ? e.error : {}) as Partial<ApiError>;
    const fallback = e.status === 0 ? 'The server cannot be reached.' : e.status === 401 ? 'Sign in first.' : e.status === 429 ? 'Too many saves at once; it retries shortly.' : `The server answered ${e.status}.`;
    return { status: e.status, code: body.code ?? `http.${e.status}`, message: body.message ?? fallback, version: body.version };
  }
  return { status: -1, code: 'client', message: e instanceof Error ? e.message : String(e) };
}

const base = '/api/iconic';

@Injectable({ providedIn: 'root' })
export class IconicApi {
  private readonly http = inject(HttpClient);

  list() {
    return firstValueFrom(this.http.get<{ projects: ProjectListItem[]; maxProjects: number }>(`${base}/projects`));
  }

  create(body: { name: string; kind: ProjectKind; width: number; height: number; scene?: Scene }) {
    return firstValueFrom(this.http.post<{ key: string; id: string; version: number }>(`${base}/projects`, body));
  }

  get(key: string) {
    return firstValueFrom(this.http.get<ProjectDto>(`${base}/projects/${enc(key)}`));
  }

  delete(key: string) {
    return firstValueFrom(this.http.delete<unknown>(`${base}/projects/${enc(key)}`));
  }

  updateMeta(key: string, body: { name?: string; kind?: ProjectKind; width?: number; height?: number }) {
    return firstValueFrom(this.http.put<unknown>(`${base}/projects/${enc(key)}/meta`, body));
  }

  saveScene(key: string, version: number, scene: Scene) {
    return firstValueFrom(this.http.put<{ version: number; usage: Usage }>(`${base}/projects/${enc(key)}/scene`, { version, scene }));
  }

  patchScene(key: string, version: number, ops: PatchOp[]) {
    return firstValueFrom(this.http.patch<{ version: number; usage: Usage }>(`${base}/projects/${enc(key)}/scene`, { version, ops }));
  }

  fileUrl(key: string, name: string): string {
    return `${base}/projects/${enc(key)}/files/${name.split('/').map(enc).join('/')}`;
  }

  putFile(key: string, name: string, bytes: Uint8Array, type: string) {
    const blob = new Blob([bytes as BlobPart], { type });
    return firstValueFrom(this.http.put<{ usage: Usage }>(this.fileUrl(key, name), blob, { headers: { 'Content-Type': type } }));
  }

  deleteFile(key: string, name: string) {
    return firstValueFrom(this.http.delete<{ usage: Usage }>(this.fileUrl(key, name)));
  }

  createSnapshot(key: string, name: string) {
    return firstValueFrom(this.http.post<{ snapshots: SnapshotInfo[]; usage: Usage }>(`${base}/projects/${enc(key)}/snapshots`, { name }));
  }

  restoreSnapshot(key: string, id: string) {
    return firstValueFrom(this.http.post<{ version: number; scene: unknown; snapshots: SnapshotInfo[]; usage: Usage }>(`${base}/projects/${enc(key)}/snapshots/${enc(id)}/restore`, {}));
  }

  deleteSnapshot(key: string, id: string) {
    return firstValueFrom(this.http.delete<{ snapshots: SnapshotInfo[]; usage: Usage }>(`${base}/projects/${enc(key)}/snapshots/${enc(id)}`));
  }

  share(key: string, enabled: boolean, newLink = false) {
    return firstValueFrom(this.http.post<{ isPublic: boolean; shareToken: string | null }>(`${base}/projects/${enc(key)}/share`, { enabled, newLink }));
  }

  shared(token: string) {
    return firstValueFrom(this.http.get<SharedDto>(`${base}/shared/${enc(token)}`));
  }

  sharedFileUrl(token: string, name: string): string {
    return `${base}/shared/${enc(token)}/files/${name.split('/').map(enc).join('/')}`;
  }

  remix(token: string) {
    return firstValueFrom(this.http.post<{ key: string; id: string }>(`${base}/shared/${enc(token)}/remix`, {}));
  }

  report(token: string, reason: string) {
    return firstValueFrom(this.http.post<unknown>(`${base}/shared/${enc(token)}/report`, { reason }));
  }

  takedown(token: string) {
    return firstValueFrom(this.http.post<{ name: string }>(`${base}/admin/takedown`, { token }));
  }

  async bytes(url: string): Promise<ArrayBuffer> {
    return firstValueFrom(this.http.get(url, { responseType: 'arraybuffer' }));
  }
}

const enc = encodeURIComponent;
