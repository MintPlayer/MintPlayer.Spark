import type * as Wire from './generated/contracts';
import type { AssertNone, MissingMembers } from './contract-mirror';
import type { ELookupDisplayType } from './generated/contracts';
import { TranslatedString } from './translated-string';

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

// ELookupDisplayType is generated (./generated/contracts). The hand-written mirrors are checked against the generated
// wire types (docs/guide-typescript-contracts.md).
export type LookupReferenceListItemMirrorsContract = AssertNone<MissingMembers<Wire.LookupReferenceListItem, LookupReferenceListItem>>;
export type LookupReferenceMirrorsContract = AssertNone<MissingMembers<Wire.LookupReferenceDto, LookupReference>>;
export type LookupReferenceValueMirrorsContract = AssertNone<MissingMembers<Wire.LookupReferenceValueDto, LookupReferenceValue>>;
