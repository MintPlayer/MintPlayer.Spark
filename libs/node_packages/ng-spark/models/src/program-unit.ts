import type * as Wire from './generated/contracts';
import type { AssertNone, MissingMembers } from './contract-mirror';
import { TranslatedString } from './translated-string';

export interface ProgramUnit {
  id: string;
  name: TranslatedString;
  icon?: string;
  /**
   * Canonical unit type, exact-cased by the server's loader: 'query' | 'persistentObject' | 'url'.
   * Kept as string rather than a union so an older client tolerates a newer server.
   */
  type: string;
  queryId?: string;
  persistentObjectId?: string;
  /**
   * For a persistentObject unit: the specific object to open — the menu entry deep-links to
   * `/po/{type}/{objectId}`. Absent means the type's default list. For a composed page this is
   * whatever stable string the app declared; the server's Actions class may ignore it.
   */
  objectId?: string;
  /**
   * For a url unit: an app path (`/merge-queue/setup`) or an absolute http(s) URL. `sparkAppUrl` decides against the
   * application's base: a link into it is a router link in the same tab, any other opens in a new tab.
   */
  url?: string;
  order: number;
  alias?: string;
}

export interface ProgramUnitGroup {
  id: string;
  name: TranslatedString;
  icon?: string;
  order: number;
  programUnits: ProgramUnit[];
}

export interface ProgramUnitsConfiguration {
  programUnitGroups: ProgramUnitGroup[];
}

// The hand-written mirrors are checked against the generated wire types (docs/guide-typescript-contracts.md).
export type ProgramUnitMirrorsContract = AssertNone<MissingMembers<Wire.ProgramUnit, ProgramUnit>>;
export type ProgramUnitGroupMirrorsContract = AssertNone<MissingMembers<Wire.ProgramUnitGroup, ProgramUnitGroup>>;
export type ProgramUnitsConfigurationMirrorsContract = AssertNone<MissingMembers<Wire.ProgramUnitsConfiguration, ProgramUnitsConfiguration>>;
