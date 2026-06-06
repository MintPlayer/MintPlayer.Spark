import { PersistentObjectAttribute } from './persistent-object-attribute';

export interface PersistentObject {
  id: string;
  name: string;
  objectTypeId: string;
  breadcrumb?: string;
  attributes: PersistentObjectAttribute[];
}
