import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface AccountInfo {
  login: string;
  type: string;
  avatarUrl?: string;
  installed: boolean;
  repoCount: number;
  aggregateCoverage?: number;
}

export interface AccountsResponse {
  /** Where to send someone to connect a forge to an account. Per-environment. */
  connectUrl: string;
  accounts: AccountInfo[];
  /**
   * A linked forge's stored credential is dead and silent refresh failed — only a browser
   * round-trip can fix it. Set if ANY linked forge needs it, so a missing row always has a
   * visible explanation. While set, accounts is degraded.
   */
  reauthRequired?: boolean;
}

@Injectable({ providedIn: 'root' })
export class AccountsService {
  private readonly http = inject(HttpClient);

  getMyAccounts(): Promise<AccountsResponse> {
    return firstValueFrom(this.http.get<AccountsResponse>('/api/me/accounts'));
  }

  /** Drops the server's cached GitHub visibility and returns the fresh list. */
  resync(): Promise<AccountsResponse> {
    return firstValueFrom(this.http.post<AccountsResponse>('/api/me/accounts/resync', {}));
  }
}
