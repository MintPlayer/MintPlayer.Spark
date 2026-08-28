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
  /** For a url unit: the external address, rendered as a plain anchor (never a router link). */
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
