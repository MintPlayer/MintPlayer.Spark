import { InjectionToken, Provider } from '@angular/core';

/** `GET/POST /spark/auth/manage/profile` (#460, D16). */
export interface SparkAccountProfile {
  userName: string | null;
  email: string | null;
  /** App-contributed fields (`ISparkProfileContributor<TUser>`), by name. */
  fields: Record<string, unknown>;
  /** The culture account mail is written in (`SparkUser.PreferredCulture`); null = the app default. */
  preferredCulture: string | null;
}

/** The body of `POST /spark/auth/manage/profile`. Absent keeps a value; `preferredCulture: null` clears it. */
export interface SparkAccountProfileUpdate {
  userName?: string | null;
  fields?: Record<string, unknown>;
  preferredCulture?: string | null;
}

/** `GET /spark/auth/manage/info` / answer of `POST manage/info`. */
export interface SparkAccountInfo {
  email: string;
  isEmailConfirmed: boolean;
}

/** Answer of `POST /spark/auth/manage/2fa` (ASP.NET Core Identity's contract). */
export interface SparkTwoFactorState {
  sharedKey: string | null;
  recoveryCodesLeft: number;
  /** Only present right after codes were (re)generated — show them once. */
  recoveryCodes?: string[] | null;
  isTwoFactorEnabled: boolean;
  isMachineRemembered: boolean;
}

/** The body of `POST /spark/auth/manage/2fa`. An empty body reads the state (and creates a key if none). */
export interface SparkTwoFactorRequest {
  enable?: boolean;
  twoFactorCode?: string;
  resetSharedKey?: boolean;
  resetRecoveryCodes?: boolean;
  forgetMachine?: boolean;
}

/** `GET /spark/auth/manage/2fa/authenticator-uri`: the key, its otpauth URI and a server-rendered QR SVG. */
export interface SparkAuthenticatorUri {
  sharedKey: string;
  authenticatorUri: string;
  qrCodeSvg: string;
}

/**
 * The outcome of an account call that can fail with a message for the user.
 *
 * `errors` flattens an RFC 7807 validation problem (Identity codes, `PreferredCulture`, contributor
 * fields) into `field → messages`; `error` carries a Spark error code (`reauthentication_required`,
 * `no_authenticator_key`, …) when the server sent one.
 */
export interface SparkAccountResult<T = void> {
  success: boolean;
  value?: T;
  error?: string;
  errors?: Record<string, string[]>;
  status?: number;
}

/**
 * One application field on the profile page (#460, D16), validated and stored server-side by an
 * `ISparkProfileContributor<TUser>` declaring the same `name`.
 */
export interface SparkAccountProfileField {
  /** The field name, as the contributor declares it (matched case-insensitively by the server). */
  name: string;
  /** A translation key (looked up with the `t` pipe) or literal text. */
  label: string;
  type?: 'text' | 'textarea' | 'email' | 'url' | 'tel' | 'number' | 'date' | 'checkbox' | 'select';
  /** For `select`. `label` is a translation key or literal text. */
  options?: { value: string; label: string }[];
  required?: boolean;
  maxLength?: number;
  /** A translation key or literal text shown under the control. */
  hint?: string;
  /** Ascending; unordered fields follow in registration order. */
  order?: number;
}

/** Multi-provider: the application's profile fields. Use {@link provideSparkAccountProfileFields}. */
export const SPARK_ACCOUNT_PROFILE_FIELDS = new InjectionToken<SparkAccountProfileField[]>('SPARK_ACCOUNT_PROFILE_FIELDS');

/** Registers profile fields; every call adds. */
export function provideSparkAccountProfileFields(...fields: SparkAccountProfileField[]): Provider[] {
  return fields.map(field => ({ provide: SPARK_ACCOUNT_PROFILE_FIELDS, useValue: field, multi: true }));
}
