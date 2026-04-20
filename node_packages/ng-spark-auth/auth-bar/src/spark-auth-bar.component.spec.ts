import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';

import { SparkAuthBarComponent } from './spark-auth-bar.component';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

function configure() {
  const auth: any = {
    logout: vi.fn().mockResolvedValue(undefined),
    isAuthenticated: () => true,
    user: () => ({ isAuthenticated: true, userName: 'jane', email: 'jane@example.com', roles: [] }),
  };
  TestBed.configureTestingModule({
    imports: [SparkAuthBarComponent],
    providers: [
      provideRouter([]),
      { provide: SparkAuthService, useValue: auth },
      { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
    ],
  });
  const fixture = TestBed.createComponent(SparkAuthBarComponent);
  const router = TestBed.inject(Router);
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockReturnValue(Promise.resolve(true));
  return { fixture, auth, navigateByUrl };
}

describe('SparkAuthBarComponent', () => {
  it('exposes the SparkAuthService for template use', () => {
    const { fixture, auth } = configure();
    expect(fixture.componentInstance.authService).toBe(auth);
  });

  it('onLogout calls authService.logout and navigates to /', async () => {
    const { fixture, auth, navigateByUrl } = configure();

    await fixture.componentInstance.onLogout();

    expect(auth.logout).toHaveBeenCalledOnce();
    expect(navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('does not navigate when logout rejects (current implementation has no try/catch)', async () => {
    const { fixture, navigateByUrl } = configure();
    fixture.componentInstance.authService.logout = vi.fn().mockRejectedValue(new Error('network'));

    await expect(fixture.componentInstance.onLogout()).rejects.toThrow();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });
});
