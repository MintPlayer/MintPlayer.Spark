import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { map, switchMap } from 'rxjs/operators';
import { EntityType, PersistentObject, ProgramUnitsConfiguration, SparkQuery } from '../models';

@Injectable({ providedIn: 'root' })
export class SparkService {
  private baseUrl = '/spark';

  constructor(private http: HttpClient) {}

  // Entity Types
  getEntityTypes(): Observable<EntityType[]> {
    return this.http.get<EntityType[]>(`${this.baseUrl}/types`);
  }

  getEntityType(id: string): Observable<EntityType> {
    return this.http.get<EntityType>(`${this.baseUrl}/types/${id}`);
  }

  // Queries
  getQueries(): Observable<SparkQuery[]> {
    return this.http.get<SparkQuery[]>(`${this.baseUrl}/queries`);
  }

  getQuery(id: string): Observable<SparkQuery> {
    return this.http.get<SparkQuery>(`${this.baseUrl}/queries/${id}`);
  }

  getQueryByName(name: string): Observable<SparkQuery | undefined> {
    return this.getQueries().pipe(
      map(queries => queries.find(q => q.name === name))
    );
  }

  executeQuery(queryId: string): Observable<PersistentObject[]> {
    return this.http.get<PersistentObject[]>(`${this.baseUrl}/queries/${queryId}/execute`);
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
    return this.http.get<PersistentObject[]>(`${this.baseUrl}/po/${type}`);
  }

  get(type: string, id: string): Observable<PersistentObject> {
    return this.http.get<PersistentObject>(`${this.baseUrl}/po/${type}/${id}`);
  }

  create(type: string, data: Partial<PersistentObject>): Observable<PersistentObject> {
    return this.http.post<PersistentObject>(`${this.baseUrl}/po/${type}`, data);
  }

  update(type: string, id: string, data: Partial<PersistentObject>): Observable<PersistentObject> {
    return this.http.put<PersistentObject>(`${this.baseUrl}/po/${type}/${id}`, data);
  }

  delete(type: string, id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/po/${type}/${id}`);
  }
}
