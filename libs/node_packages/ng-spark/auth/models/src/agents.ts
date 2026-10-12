/**
 * What an AI agent may do, as the server writes `EAgentPermission`: `'Read'`, `'Read, Write'`, or `'None'`
 * (MCP PRD D6). "Write" is shown to users as "changes".
 */
export type SparkAgentPermission = 'None' | 'Read' | 'Write' | 'Read, Write';

/** Whether `permission` includes changes (`Write`). */
export function sparkAgentCanWrite(permission: SparkAgentPermission | null | undefined): boolean {
  return !!permission && permission.split(',').some(p => p.trim() === 'Write');
}

/** One chosen type of a grant. */
export interface SparkAgentGrantType {
  name: string;
  permissions: SparkAgentPermission;
}

/** An entity type the application makes available to agents and the user may see, with its own most. */
export interface SparkAgentExposedType {
  name: string;
  label: string;
  permissions: SparkAgentPermission;
  /** Labels of the type's queries, shown under it. */
  queries: string[];
}

/**
 * One agent grant (`AgentGrant`): what the user allowed one agent. `types: null` means everything the
 * application makes available to agents, including types exposed later.
 */
export interface SparkAgentGrant {
  /** Identifies the grant in requests (`pat/…`, `ext/…`, `app/…`); the user's own grants only. */
  key: string;
  /** `PersonalAccessToken`, `MetadataDocument`, `DynamicRegistration`, `PreRegistered`, `ExternalAuthorizationServer`. */
  kind: string;
  /** A token's name, or the name an OAuth client gives itself (unverified unless `verifiedHost` is set). */
  name: string | null;
  clientId: string | null;
  /** Set only when the client's identity was verified (its metadata document was fetched from this host). */
  verifiedHost: string | null;
  /** For `ExternalAuthorizationServer`: the issuer of the server the agent signed in at. */
  issuer?: string | null;
  permissions: SparkAgentPermission;
  types: SparkAgentGrantType[] | null;
  /** The most this grant may be given (the application's limit for this agent). */
  maxPermissions: SparkAgentPermission;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  /**
   * `revoked`: the token stopped working because the password or sign-in methods changed. `disconnected`: an external
   * authorization server's agent the user disconnected, or one a password or sign-in-method change disconnected; an
   * edit allows it again.
   */
  status: 'active' | 'expired' | 'revoked' | 'disconnected';
}

/** An entry another package contributed (e.g. sign-ins at other websites, from the identity provider). */
export interface SparkContributedApplication {
  source: string;
  id: string;
  name: string;
  description: string | null;
  createdAt: string | null;
  lastUsedAt: string | null;
}

/** `GET /spark/auth/manage/agents`. */
export interface SparkAgentGrantsOverview {
  /** Whether personal access tokens can be created here. */
  personalAccessTokens: boolean;
  defaultTokenLifetimeDays: number;
  maxTokenLifetimeDays: number;
  /** The most a new token may be granted. */
  tokenPermissions: SparkAgentPermission;
  types: SparkAgentExposedType[];
  grants: SparkAgentGrant[];
  applications: SparkContributedApplication[];
}

/** The body of `POST /spark/auth/manage/agents/tokens`. Omit `types` for everything exposed. */
export interface SparkAgentTokenRequest {
  name: string;
  expiresInDays?: number;
  permissions: SparkAgentPermission;
  types?: SparkAgentGrantType[] | null;
}

/** The answer of `POST /spark/auth/manage/agents/tokens`: the only time the token is ever shown. */
export interface SparkAgentTokenCreated {
  token: string;
  key: string;
  expiresAt: string;
}

/** The body of `POST /spark/auth/manage/agents/update`. `types: null` for everything exposed. */
export interface SparkAgentGrantUpdate {
  key: string;
  permissions: SparkAgentPermission;
  types: SparkAgentGrantType[] | null;
}
