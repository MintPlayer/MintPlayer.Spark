/** A call's outcome: the value, or the HTTP status and the server's `{ error }` text (400, 409). Calls never throw. */
export type SparkAppsResult<T> = { ok: true; value: T } | { ok: false; status: number; error: string | null };

/** Who owns webhooks, Apps and installations (`WebhookOwnerDescription`): `user`, `organization`, or a site's own kind. */
export interface SparkWebhookOwner {
  kind: string;
  key: string;
  displayName: string;
}

export interface SparkWebhookEndpoint {
  id: string;
  ownerKind: string;
  ownerKey: string;
  url: string;
  description: string | null;
  events: string[];
  enabled: boolean;
  disabledReason: string | null;
  createdUtc: string;
  lastSuccessUtc: string | null;
  failingSinceUtc: string | null;
  /** While a rotated-out secret still signs alongside the new one. */
  previousSecretExpiresUtc: string | null;
}

/** A new or rotated webhook and its secret, which the server shows this once. */
export interface SparkWebhookEndpointSecret {
  endpoint: SparkWebhookEndpoint;
  secret: string;
}

export interface SparkWebhookEndpointInput {
  ownerKind: string;
  ownerKey: string;
  url: string;
  description?: string | null;
  events: string[];
}

/** Null leaves a field as it is. */
export interface SparkWebhookEndpointChange {
  id: string;
  url?: string | null;
  description?: string | null;
  events?: string[] | null;
}

export type SparkWebhookDeliveryStatus = 'Pending' | 'Succeeded' | 'Failed';

export interface SparkWebhookDeliveryAttempt {
  atUtc: string;
  statusCode: number | null;
  error: string | null;
  durationMs: number;
  responseBody: string | null;
}

export interface SparkWebhookDelivery {
  id: string;
  messageId: string;
  eventType: string;
  status: SparkWebhookDeliveryStatus;
  createdUtc: string;
  completedUtc: string | null;
  attempts: SparkWebhookDeliveryAttempt[];
  redeliveryOf: string | null;
  /** The signed body, as sent. */
  payload: string;
}

/** One event of the catalog (`GET /spark/apps/webhooks/catalog`). */
export interface SparkWebhookEventInfo {
  name: string;
  summary: string;
  /** What an App needs to receive it (`resource:read`), or null for any App. */
  permission: string | null;
}

/** The catalog, reduced to what the settings pages offer. */
export interface SparkWebhookCatalog {
  events: SparkWebhookEventInfo[];
  /** Every permission an App may ask for that means something on this site. */
  permissions: string[];
}

export interface SparkAppKey {
  keyId: string;
  fingerprint: string;
  createdUtc: string;
}

/** An App as its managers see it. */
export interface SparkApp {
  slug: string;
  name: string;
  description: string | null;
  homepageUrl: string | null;
  ownerKind: string;
  ownerKey: string;
  clientId: string;
  public: boolean;
  permissions: string[];
  events: string[];
  webhookEndpointId: string | null;
  webhookUrl: string | null;
  setupUrl: string | null;
  keys: SparkAppKey[];
  createdUtc: string;
}

/** An App as anyone who may install it sees it: no keys, no webhook. */
export interface SparkAppPublic {
  slug: string;
  name: string;
  description: string | null;
  homepageUrl: string | null;
  ownerKind: string;
  ownerKey: string;
  permissions: string[];
  events: string[];
}

/** A registered or changed App; `webhookSecret` when it got a new webhook, shown this once. */
export interface SparkAppCreated {
  app: SparkApp;
  webhookSecret: string | null;
}

/** A new key: the PKCS#8 PEM private key, shown this once. */
export interface SparkAppKeyCreated {
  key: SparkAppKey;
  privateKeyPem: string;
}

/** The fields of an App's form; `ownerKind`/`ownerKey` only when registering. */
export interface SparkAppFields {
  slug: string;
  name: string;
  description: string | null;
  homepageUrl: string | null;
  webhookUrl: string | null;
  setupUrl: string | null;
  public: boolean;
  permissions: string[];
  events: string[];
}

export interface SparkAppInput extends SparkAppFields {
  ownerKind: string;
  ownerKey: string;
}

export interface SparkAppInstallation {
  id: string;
  appSlug: string;
  appName: string;
  targetKind: string;
  targetKey: string;
  /** What the installer granted. */
  permissions: string[];
  /** What the App asks for since, and the installer has not accepted yet. */
  pendingPermissions: string[];
  /** The events it receives: the App's, as far as the granted permissions cover them. */
  events: string[];
  suspended: boolean;
  installedBy: string;
  installedUtc: string;
}

/** Where the user may install an App; `installationId` when it is installed there already. */
export interface SparkAppInstallTarget {
  kind: string;
  key: string;
  displayName: string;
  installationId: string | null;
}

/** The consent screen: what the App asks for, and where the user may install it. */
export interface SparkAppInstallPrompt {
  app: SparkAppPublic;
  targets: SparkAppInstallTarget[];
}

/** A new installation, and the App's setup URL (with `installation_id`) to send the installer to, if it has one. */
export interface SparkAppInstalled {
  installation: SparkAppInstallation;
  redirectUrl: string | null;
}
