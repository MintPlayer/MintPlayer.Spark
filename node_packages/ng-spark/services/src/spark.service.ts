import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { CustomActionDefinition, EntityPermissions, EntityType, LookupReference, LookupReferenceListItem, LookupReferenceValue, PersistentObject, ProgramUnitsConfiguration, QueryResult, SparkQuery, RetryActionPayload, RetryActionResult } from '@mintplayer/ng-spark/models';
import { SortColumn } from '@mintplayer/pagination';
import { RetryActionService } from './retry-action.service';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

@Injectable({ providedIn: 'root' })
export class SparkService {
  private readonly config = inject(SPARK_CONFIG, { optional: true });
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly http = inject(HttpClient);
  private readonly retryActionService = inject(RetryActionService);

  // Entity Types
  async getEntityTypes(): Promise<EntityType[]> {
    return firstValueFrom(this.http.get<EntityType[]>(`${this.baseUrl}/types`));
  }

  async getEntityType(id: string): Promise<EntityType> {
    return firstValueFrom(this.http.get<EntityType>(`${this.baseUrl}/types/${encodeURIComponent(id)}`));
  }

  async getEntityTypeByClrType(clrType: string): Promise<EntityType | undefined> {
    const types = await this.getEntityTypes();
    return types.find(t => t.clrType === clrType);
  }

  // Permissions
  async getPermissions(entityTypeId: string): Promise<EntityPermissions> {
    return firstValueFrom(this.http.get<EntityPermissions>(`${this.baseUrl}/permissions/${encodeURIComponent(entityTypeId)}`));
  }

  // Queries
  async getQueries(): Promise<SparkQuery[]> {
    return firstValueFrom(this.http.get<SparkQuery[]>(`${this.baseUrl}/queries`));
  }

  async getQuery(id: string): Promise<SparkQuery> {
    return firstValueFrom(this.http.get<SparkQuery>(`${this.baseUrl}/queries/${encodeURIComponent(id)}`));
  }

  async getQueryByName(name: string): Promise<SparkQuery | undefined> {
    const queries = await this.getQueries();
    return queries.find(q => q.name === name);
  }

  async executeQuery(queryId: string, options?: {
    sortColumns?: SortColumn[];
    parentId?: string;
    parentType?: string;
    skip?: number;
    take?: number;
    search?: string;
  }): Promise<QueryResult> {
    let params = new HttpParams();
    if (options?.sortColumns?.length) {
      params = params.set('sortColumns',
        options.sortColumns.map(c => `${c.property}:${c.direction === 'descending' ? 'desc' : 'asc'}`).join(',')
      );
    }
    if (options?.parentId) params = params.set('parentId', options.parentId);
    if (options?.parentType) params = params.set('parentType', options.parentType);
    if (options?.skip != null) params = params.set('skip', options.skip);
    if (options?.take != null) params = params.set('take', options.take);
    if (options?.search) params = params.set('search', options.search);
    return firstValueFrom(this.http.get<QueryResult>(
      `${this.baseUrl}/queries/${encodeURIComponent(queryId)}/execute`,
      { params }
    ));
  }

  async executeQueryByName(queryName: string, options?: {
    parentId?: string;
    parentType?: string;
  }): Promise<QueryResult> {
    const query = await this.getQueryByName(queryName);
    return query ? this.executeQuery(query.id, { parentId: options?.parentId, parentType: options?.parentType }) : { data: [], totalRecords: 0, skip: 0, take: 50 };
  }

  // Program Units
  async getProgramUnits(): Promise<ProgramUnitsConfiguration> {
    return firstValueFrom(this.http.get<ProgramUnitsConfiguration>(`${this.baseUrl}/program-units`));
  }

  // Persistent Objects
  async list(type: string): Promise<PersistentObject[]> {
    return firstValueFrom(this.http.get<PersistentObject[]>(`${this.baseUrl}/po/${encodeURIComponent(type)}`));
  }

  async get(type: string, id: string): Promise<PersistentObject> {
    return firstValueFrom(this.http.get<PersistentObject>(`${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`));
  }

  async create(type: string, data: Partial<PersistentObject>): Promise<PersistentObject> {
    return this.postWithRetry<PersistentObject>(
      `${this.baseUrl}/po/${encodeURIComponent(type)}`,
      { persistentObject: data }
    );
  }

  async update(type: string, id: string, data: Partial<PersistentObject>): Promise<PersistentObject> {
    return this.putWithRetry<PersistentObject>(
      `${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`,
      { persistentObject: data }
    );
  }

  async delete(type: string, id: string): Promise<void> {
    return this.deleteWithRetry<void>(
      `${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`,
      {}
    );
  }

  // Custom Actions
  async getCustomActions(objectTypeId: string): Promise<CustomActionDefinition[]> {
    return firstValueFrom(this.http.get<CustomActionDefinition[]>(`${this.baseUrl}/actions/${encodeURIComponent(objectTypeId)}`));
  }

  async executeCustomAction(objectTypeId: string, actionName: string, parent?: PersistentObject, selectedItems?: PersistentObject[]): Promise<void> {
    const body: { parent?: PersistentObject; selectedItems?: PersistentObject[]; retryResults?: RetryActionResult[] } = { parent, selectedItems };
    return this.postWithRetry<void>(
      `${this.baseUrl}/actions/${encodeURIComponent(objectTypeId)}/${encodeURIComponent(actionName)}`,
      body as any
    );
  }

  // LookupReferences
  async getLookupReferences(): Promise<LookupReferenceListItem[]> {
    return firstValueFrom(this.http.get<LookupReferenceListItem[]>(`${this.baseUrl}/lookupref`));
  }

  async getLookupReference(name: string): Promise<LookupReference> {
    return firstValueFrom(this.http.get<LookupReference>(`${this.baseUrl}/lookupref/${encodeURIComponent(name)}`));
  }

  async addLookupReferenceValue(name: string, value: LookupReferenceValue): Promise<LookupReferenceValue> {
    return firstValueFrom(this.http.post<LookupReferenceValue>(`${this.baseUrl}/lookupref/${encodeURIComponent(name)}`, value));
  }

  async updateLookupReferenceValue(name: string, key: string, value: LookupReferenceValue): Promise<LookupReferenceValue> {
    return firstValueFrom(this.http.put<LookupReferenceValue>(
      `${this.baseUrl}/lookupref/${encodeURIComponent(name)}/${encodeURIComponent(key)}`,
      value
    ));
  }

  async deleteLookupReferenceValue(name: string, key: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(
      `${this.baseUrl}/lookupref/${encodeURIComponent(name)}/${encodeURIComponent(key)}`
    ));
  }

  // Retry Action helpers

  private async postWithRetry<T>(url: string, body: { persistentObject?: any; retryResults?: RetryActionResult[] }): Promise<T> {
    try {
      return await firstValueFrom(this.http.post<T>(url, body));
    } catch (error) {
      return this.handleRetryError<T>(error as HttpErrorResponse, () => this.postWithRetry<T>(url, body), body);
    }
  }

  private async putWithRetry<T>(url: string, body: { persistentObject?: any; retryResults?: RetryActionResult[] }): Promise<T> {
    try {
      return await firstValueFrom(this.http.put<T>(url, body));
    } catch (error) {
      return this.handleRetryError<T>(error as HttpErrorResponse, () => this.putWithRetry<T>(url, body), body);
    }
  }

  private async deleteWithRetry<T>(url: string, body: { retryResults?: RetryActionResult[] }): Promise<T> {
    try {
      const hasRetry = body.retryResults && body.retryResults.length > 0;
      return await firstValueFrom(
        hasRetry
          ? this.http.delete<T>(url, { body })
          : this.http.delete<T>(url)
      );
    } catch (error) {
      return this.handleRetryError<T>(error as HttpErrorResponse, () => this.deleteWithRetry<T>(url, body), body);
    }
  }

  private async handleRetryError<T>(
    error: HttpErrorResponse,
    retryFn: () => Promise<T>,
    body: { retryResults?: RetryActionResult[] }
  ): Promise<T> {
    if (error.status !== 449 || error.error?.type !== 'retry-action') {
      throw error;
    }

    const payload = error.error as RetryActionPayload;
    const result = await this.retryActionService.show(payload);

    // Modal dismissed (Escape/X) - Cancel was not an explicit developer option
    if (result.option === 'Cancel' && !payload.options.includes('Cancel')) {
      throw error;
    }

    body.retryResults = [...(body.retryResults || []), result];
    return retryFn();
  }
}
