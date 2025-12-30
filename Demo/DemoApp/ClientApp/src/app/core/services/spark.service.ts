import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { map, switchMap } from 'rxjs/operators';
import { EntityType, PersistentObject, ProgramUnitsConfiguration, SparkQuery } from '../models';

@Injectable({ providedIn: 'root' })
export class SparkService {
  private readonly baseUrl = '/spark';
  private readonly http = inject(HttpClient);

  // Entity Types
  getEntityTypes(): Observable<EntityType[]> {
    return this.http.get<EntityType[]>(`${this.baseUrl}/types`);
  }

  getEntityType(id: string): Observable<EntityType> {
    return this.http.get<EntityType>(`${this.baseUrl}/types/${encodeURIComponent(id)}`);
  }

  getEntityTypeByClrType(clrType: string): Observable<EntityType | undefined> {
    return this.getEntityTypes().pipe(
      map(types => types.find(t => t.clrType === clrType))
    );
  }

  // Queries
  getQueries(): Observable<SparkQuery[]> {
    return this.http.get<SparkQuery[]>(`${this.baseUrl}/queries`);
  }

  getQuery(id: string): Observable<SparkQuery> {
    return this.http.get<SparkQuery>(`${this.baseUrl}/queries/${encodeURIComponent(id)}`);
  }

  getQueryByName(name: string): Observable<SparkQuery | undefined> {
    return this.getQueries().pipe(
      map(queries => queries.find(q => q.name === name))
    );
  }

  executeQuery(queryId: string): Observable<PersistentObject[]> {
    return this.http.get<PersistentObject[]>(`${this.baseUrl}/queries/${encodeURIComponent(queryId)}/execute`);
  }

  executeQueryByName(queryName: string): Observable<PersistentObject[]> {
    return this.getQueryByName(queryName).pipe(
      switchMap(query => query ? this.executeQuery(query.id) : of([]))
    );
  }

  // Program Units
  getProgramUnits(): Observable<ProgramUnitsConfiguration> {
    return this.http.get<ProgramUnitsConfiguration>(`${this.baseUrl}/program-units`);
  }

  // Persistent Objects
  list(type: string): Observable<PersistentObject[]> {
    return this.http.get<PersistentObject[]>(`${this.baseUrl}/po/${encodeURIComponent(type)}`);
  }

  get(type: string, id: string): Observable<PersistentObject> {
    return this.http.get<PersistentObject>(`${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`);
  }

  create(type: string, data: Partial<PersistentObject>): Observable<PersistentObject> {
    return this.http.post<PersistentObject>(`${this.baseUrl}/po/${encodeURIComponent(type)}`, data);
  }

  update(type: string, id: string, data: Partial<PersistentObject>): Observable<PersistentObject> {
    return this.http.put<PersistentObject>(`${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`, data);
  }

  delete(type: string, id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/po/${encodeURIComponent(type)}/${encodeURIComponent(id)}`);
  }
}
