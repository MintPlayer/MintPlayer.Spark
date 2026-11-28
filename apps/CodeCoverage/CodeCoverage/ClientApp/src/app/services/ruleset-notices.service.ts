import { Injectable, Injector, computed, effect, inject, untracked } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_AUTH_STATE, sparkSignedIn } from '@mintplayer/ng-spark';

/** What `POST /api/me/ruleset-notices` answers. */
export interface RulesetNoticesDelivered {
  /** How many bell notifications were sent; they arrive in the bell over the live socket. */
  delivered: number;
}

/**
 * #714 Dec5, Dec11: asks the server, each time the app becomes signed in (when it opens with a session, and right after a
 * sign-in), to turn the merge-queue ruleset notices of the repositories the viewer manages into bell notifications.
 *
 * The server decides everything: who manages what, and who was told already. A failure is ignored, because the next
 * sign-in or app open asks again.
 */
@Injectable({ providedIn: 'root' })
export class RulesetNoticesService {
  private readonly http = inject(HttpClient);
  private readonly injector = inject(Injector);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });
  // A computed boolean, so a refreshed user object that stays signed in posts nothing.
  private readonly signedIn = computed(() => sparkSignedIn(this.authState?.()));
  private started = false;

  /** Follows the sign-in state: one delivery each time it turns signed in. Called once, at startup. */
  start(): void {
    if (this.started) return;
    this.started = true;
    effect(() => {
      if (this.signedIn()) untracked(() => void this.deliver());
    }, { injector: this.injector });
  }

  /** Delivers the viewer's waiting notices; resolves how many, or 0 when the request failed. */
  deliver(): Promise<number> {
    return firstValueFrom(this.http.post<RulesetNoticesDelivered>('/api/me/ruleset-notices', {}))
      .then(result => result.delivered, () => 0);
  }
}
