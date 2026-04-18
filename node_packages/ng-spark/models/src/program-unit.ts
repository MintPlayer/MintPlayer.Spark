import { TranslatedString } from './translated-string';

export interface ProgramUnit {
  id: string;
  name: TranslatedString;
  icon?: string;
  type: string;
  queryId?: string;
  persistentObjectId?: string;
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
