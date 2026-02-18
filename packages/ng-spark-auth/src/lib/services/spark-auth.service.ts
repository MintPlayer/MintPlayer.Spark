import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { AuthUser, SPARK_AUTH_CONFIG } from '../models';

@Injectable({ providedIn: 'root' })
export class SparkAuthService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SPARK_AUTH_CONFIG);

  private readonly currentUser = signal<AuthUser | null>(null);

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser()?.isAuthenticated === true);

  constructor() {
    this.checkAuth().subscribe();
  }

  login(email: string, password: string): Observable<void> {
    return this.http
      .post<void>(`${this.config.apiBasePath}/login?useCookies=true`, { email, password })
      .pipe(tap(() => { this.checkAuth().subscribe(); }));
  }

  loginTwoFactor(twoFactorCode: string, twoFactorRecoveryCode?: string): Observable<void> {
    return this.http
      .post<void>(`${this.config.apiBasePath}/login?useCookies=true`, {
        twoFactorCode,
        twoFactorRecoveryCode,
      })
      .pipe(tap(() => { this.checkAuth().subscribe(); }));
  }

  register(email: string, password: string): Observable<void> {
    return this.http.post<void>(`${this.config.apiBasePath}/register`, { email, password });
  }

  logout(): Observable<void> {
    return this.http
      .post<void>(`${this.config.apiBasePath}/logout`, {})
      .pipe(tap(() => { this.currentUser.set(null); }));
  }

  checkAuth(): Observable<AuthUser | null> {
    return this.http.get<AuthUser>(`${this.config.apiBasePath}/me`).pipe(
      tap({
        next: (user) => this.currentUser.set(user),
        error: () => this.currentUser.set(null),
      }),
    );
  }

  forgotPassword(email: string): Observable<void> {
    return this.http.post<void>(`${this.config.apiBasePath}/forgotPassword`, { email });
  }

  resetPassword(email: string, resetCode: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.config.apiBasePath}/resetPassword`, {
      email,
      resetCode,
      newPassword,
    });
  }
}
