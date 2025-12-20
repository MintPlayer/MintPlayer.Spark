import { PersistentObjectAttribute } from './persistent-object-attribute';

export interface PersistentObject {
  id: string;
  name: string;
  clrType: string;
  breadcrumb?: string;
  attributes: PersistentObjectAttribute[];
}
