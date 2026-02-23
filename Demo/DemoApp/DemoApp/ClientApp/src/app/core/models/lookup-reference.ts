import { TranslatedString } from './translated-string';

export enum ELookupDisplayType {
  Dropdown = 0,
  Modal = 1
}

export interface LookupReferenceListItem {
  name: string;
  isTransient: boolean;
  valueCount: number;
  displayType: ELookupDisplayType;
}

export interface LookupReference {
  name: string;
  isTransient: boolean;
  displayType: ELookupDisplayType;
  values: LookupReferenceValue[];
}

export interface LookupReferenceValue {
  key: string;
  values: TranslatedString;
  isActive: boolean;
  extra?: Record<string, unknown>;
}
