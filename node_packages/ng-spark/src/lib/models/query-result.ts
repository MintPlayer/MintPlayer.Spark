import { PersistentObject } from './persistent-object';

export interface QueryResult {
  data: PersistentObject[];
  totalRecords: number;
  skip: number;
  take: number;
}
