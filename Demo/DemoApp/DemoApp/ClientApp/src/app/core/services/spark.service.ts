import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { EntityPermissions, EntityType, LookupReference, LookupReferenceListItem, LookupReferenceValue, PersistentObject, ProgramUnitsConfiguration, SparkQuery } from '../models';
import { RetryActionResult } from '../models/retry-action';
import { RetryActionService } from './retry-action.service';

@Injectable({ providedIn: 'root' })
export class SparkService {
  private readonly baseUrl = '/spark';
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

  async executeQuery(queryId: string, sortBy?: string, sortDirection?: string): Promise<PersistentObject[]> {
    let params = new HttpParams();
    if (sortBy) params = params.set('sortBy', sortBy);
    if (sortDirection) params = params.set('sortDirection', sortDirection);
    return firstValueFrom(this.http.get<PersistentObject[]>(
      `${this.baseUrl}/queries/${encodeURIComponent(queryId)}/execute`,
      { params }
    ));
  }

  async executeQueryByName(queryName: string): Promise<PersistentObject[]> {
    const query = await this.getQueryByName(queryName);
    return query ? this.executeQuery(query.id) : [];
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
    return firstValueFrom(this.http.delete<void>(`${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`));
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

  private async postWithRetry<T>(url: string, body: { persistentObject: any; retryResult?: RetryActionResult }): Promise<T> {
    try {
      return await firstValueFrom(this.http.post<T>(url, body));
    } catch (error) {
      return this.handleRetryError<T>(error as HttpErrorResponse, () => this.postWithRetry<T>(url, body), body);
    }
  }

  private async putWithRetry<T>(url: string, body: { persistentObject: any; retryResult?: RetryActionResult }): Promise<T> {
    try {
      return await firstValueFrom(this.http.put<T>(url, body));
    } catch (error) {
      return this.handleRetryError<T>(error as HttpErrorResponse, () => this.putWithRetry<T>(url, body), body);
    }
  }

  private async handleRetryError<T>(
    error: HttpErrorResponse,
    retryFn: () => Promise<T>,
    body: { persistentObject: any; retryResult?: RetryActionResult }
  ): Promise<T> {
    if (error.status !== 449 || error.error?.type !== 'retry-action') {
      throw error;
    }

    const payload = error.error;
    const result = await this.retryActionService.show(payload);
    body.retryResult = result;
    return retryFn();
  }
}
