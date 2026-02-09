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
  translations: Record<string, string>;
  isActive: boolean;
  extra?: Record<string, unknown>;
}
