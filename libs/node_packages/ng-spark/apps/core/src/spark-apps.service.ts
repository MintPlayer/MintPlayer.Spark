import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, type Observable } from 'rxjs';
import type {
  SparkApp,
  SparkAppCreated,
  SparkAppFields,
  SparkAppInput,
  SparkAppInstallPrompt,
  SparkAppInstallation,
  SparkAppInstalled,
  SparkAppKeyCreated,
  SparkAppPublic,
  SparkAppsResult,
  SparkWebhookCatalog,
  SparkWebhookDelivery,
  SparkWebhookEndpoint,
  SparkWebhookEndpointChange,
  SparkWebhookEndpointInput,
  SparkWebhookEndpointSecret,
  SparkWebhookEventInfo,
  SparkWebhookOwner,
} from './models';

/** The Apps API's prefix (`AppsGroup.Prefix` on the server). */
export const SPARK_APPS_API = '/spark/apps';

/**
 * The `/spark/apps` API (#691, `docs/apps_webhooks_PRD.md`): webhooks and their delivery log, Apps, and
 * installations. Every call is for a signed-in user; the POSTs need the antiforgery token, which Angular's XSRF
 * support adds (`withSparkAuth()` configures it).
 *
 * Calls never throw: each resolves to a {@link SparkAppsResult}, so a page branches on `ok` and the status.
 */
@Injectable({ providedIn: 'root' })
export class SparkAppsService {
  private readonly http = inject(HttpClient);

  // Webhooks

  /** The events and permissions of this site, read from the OpenAPI catalog codegen reads too. */
  async catalog(): Promise<SparkAppsResult<SparkWebhookCatalog>> {
    const result = await this.call(this.http.get<OpenApiCatalog>(`${SPARK_APPS_API}/webhooks/catalog`));
    return result.ok ? { ok: true, value: readCatalog(result.value) } : result;
  }

  owners(): Promise<SparkAppsResult<SparkWebhookOwner[]>> {
    return this.call(this.http.get<SparkWebhookOwner[]>(`${SPARK_APPS_API}/webhooks/owners`));
  }

  endpoints(owner: OwnerRef): Promise<SparkAppsResult<SparkWebhookEndpoint[]>> {
    return this.call(this.http.get<SparkWebhookEndpoint[]>(`${SPARK_APPS_API}/webhooks/endpoints`, { params: ownerParams(owner) }));
  }

  createEndpoint(input: SparkWebhookEndpointInput): Promise<SparkAppsResult<SparkWebhookEndpointSecret>> {
    return this.call(this.http.post<SparkWebhookEndpointSecret>(`${SPARK_APPS_API}/webhooks/endpoints`, input));
  }

  updateEndpoint(change: SparkWebhookEndpointChange): Promise<SparkAppsResult<SparkWebhookEndpoint>> {
    return this.call(this.http.post<SparkWebhookEndpoint>(`${SPARK_APPS_API}/webhooks/endpoints/update`, change));
  }

  deleteEndpoint(id: string): Promise<SparkAppsResult<null>> {
    return this.call(this.http.post<null>(`${SPARK_APPS_API}/webhooks/endpoints/delete`, { id }));
  }

  /** A new secret, shown once; the old one keeps signing alongside for a day. */
  rotateSecret(id: string): Promise<SparkAppsResult<SparkWebhookEndpointSecret>> {
    return this.call(this.http.post<SparkWebhookEndpointSecret>(`${SPARK_APPS_API}/webhooks/endpoints/rotate-secret`, { id }));
  }

  enableEndpoint(id: string): Promise<SparkAppsResult<SparkWebhookEndpoint>> {
    return this.call(this.http.post<SparkWebhookEndpoint>(`${SPARK_APPS_API}/webhooks/endpoints/enable`, { id }));
  }

  /** Sends a `ping` event; the value is the delivery's id. */
  async ping(id: string): Promise<SparkAppsResult<string>> {
    const result = await this.call(this.http.post<{ id: string }>(`${SPARK_APPS_API}/webhooks/endpoints/ping`, { id }));
    return result.ok ? { ok: true, value: result.value.id } : result;
  }

  /** The delivery log, newest first, 50 at a time; `after` is the last delivery of the previous page. */
  deliveries(endpointId: string, after?: string | null): Promise<SparkAppsResult<SparkWebhookDelivery[]>> {
    let params = new HttpParams().set('endpointId', endpointId);
    if (after) params = params.set('after', after);
    return this.call(this.http.get<SparkWebhookDelivery[]>(`${SPARK_APPS_API}/webhooks/deliveries`, { params }));
  }

  /** Sends a logged delivery again, under the same `webhook-id`; the value is the new delivery's id. */
  async redeliver(deliveryId: string): Promise<SparkAppsResult<string>> {
    const result = await this.call(this.http.post<{ id: string }>(`${SPARK_APPS_API}/webhooks/deliveries/redeliver`, { id: deliveryId }));
    return result.ok ? { ok: true, value: result.value.id } : result;
  }

  // Apps

  apps(owner: OwnerRef): Promise<SparkAppsResult<SparkApp[]>> {
    return this.call(this.http.get<SparkApp[]>(`${SPARK_APPS_API}/apps`, { params: ownerParams(owner) }));
  }

  /** The full App to its managers, the public view of a public App to anyone else. */
  app(slug: string): Promise<SparkAppsResult<SparkApp | SparkAppPublic>> {
    return this.call(this.http.get<SparkApp | SparkAppPublic>(`${SPARK_APPS_API}/apps/app`, { params: { slug } }));
  }

  createApp(input: SparkAppInput): Promise<SparkAppsResult<SparkAppCreated>> {
    return this.call(this.http.post<SparkAppCreated>(`${SPARK_APPS_API}/apps`, input));
  }

  /** An empty webhook URL removes the webhook. */
  updateApp(change: SparkAppFields): Promise<SparkAppsResult<SparkAppCreated>> {
    return this.call(this.http.post<SparkAppCreated>(`${SPARK_APPS_API}/apps/update`, change));
  }

  deleteApp(slug: string): Promise<SparkAppsResult<null>> {
    return this.call(this.http.post<null>(`${SPARK_APPS_API}/apps/delete`, { slug }));
  }

  generateKey(slug: string): Promise<SparkAppsResult<SparkAppKeyCreated>> {
    return this.call(this.http.post<SparkAppKeyCreated>(`${SPARK_APPS_API}/apps/keys/generate`, { slug }));
  }

  deleteKey(slug: string, keyId: string): Promise<SparkAppsResult<SparkApp>> {
    return this.call(this.http.post<SparkApp>(`${SPARK_APPS_API}/apps/keys/delete`, { slug, keyId }));
  }

  // Installations

  installPrompt(slug: string): Promise<SparkAppsResult<SparkAppInstallPrompt>> {
    return this.call(this.http.get<SparkAppInstallPrompt>(`${SPARK_APPS_API}/installations/new`, { params: { slug } }));
  }

  install(slug: string, target: OwnerRef): Promise<SparkAppsResult<SparkAppInstalled>> {
    return this.call(this.http.post<SparkAppInstalled>(`${SPARK_APPS_API}/installations`, { slug, targetKind: target.kind, targetKey: target.key }));
  }

  installations(target: OwnerRef): Promise<SparkAppsResult<SparkAppInstallation[]>> {
    return this.call(this.http.get<SparkAppInstallation[]>(`${SPARK_APPS_API}/installations`, {
      params: { targetKind: target.kind, targetKey: target.key },
    }));
  }

  /** Where an App is installed, for its managers. */
  appInstallations(slug: string): Promise<SparkAppsResult<SparkAppInstallation[]>> {
    return this.call(this.http.get<SparkAppInstallation[]>(`${SPARK_APPS_API}/apps/installations`, { params: { slug } }));
  }

  setSuspended(id: string, suspended: boolean): Promise<SparkAppsResult<SparkAppInstallation>> {
    return this.call(this.http.post<SparkAppInstallation>(`${SPARK_APPS_API}/installations/${suspended ? 'suspend' : 'unsuspend'}`, { id }));
  }

  /** Grants what the App asks for since it was installed. */
  acceptPermissions(id: string): Promise<SparkAppsResult<SparkAppInstallation>> {
    return this.call(this.http.post<SparkAppInstallation>(`${SPARK_APPS_API}/installations/accept`, { id }));
  }

  uninstall(id: string): Promise<SparkAppsResult<null>> {
    return this.call(this.http.post<null>(`${SPARK_APPS_API}/installations/delete`, { id }));
  }

  private async call<T>(request: Observable<T>): Promise<SparkAppsResult<T>> {
    try {
      return { ok: true, value: await firstValueFrom(request) };
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        const body = error.error as { error?: unknown } | null;
        return { ok: false, status: error.status, error: typeof body?.error === 'string' ? body.error : null };
      }
      return { ok: false, status: 0, error: null };
    }
  }
}

/** An owner or installation target, by kind and key. */
export interface OwnerRef {
  kind: string;
  key: string;
}

function ownerParams(owner: OwnerRef): HttpParams {
  return new HttpParams().set('ownerKind', owner.kind).set('ownerKey', owner.key);
}

interface OpenApiCatalog {
  webhooks?: Record<string, { post?: { summary?: string; 'x-spark-permission'?: string } }>;
  'x-spark-permissions'?: string[];
}

/** The settings pages' view of the OpenAPI document, sorted by event name. */
export function readCatalog(document: OpenApiCatalog): SparkWebhookCatalog {
  const events: SparkWebhookEventInfo[] = Object.entries(document.webhooks ?? {})
    .map(([name, item]) => ({ name, summary: item.post?.summary ?? name, permission: item.post?.['x-spark-permission'] ?? null }))
    .sort((a, b) => a.name.localeCompare(b.name));
  return { events, permissions: [...(document['x-spark-permissions'] ?? [])] };
}

/**
 * Whether `granted` covers `needed`: the permission itself, or the `write` of a `read` (`SparkAppManager.Covers`).
 */
export function permissionCovers(granted: readonly string[], needed: string | null): boolean {
  if (!needed) return true;
  if (granted.includes(needed)) return true;
  return needed.endsWith(':read') && granted.includes(needed.slice(0, -':read'.length) + ':write');
}
