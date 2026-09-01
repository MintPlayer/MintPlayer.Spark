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
  /** Public page of this environment's GitHub App ("install the App" link target). */
  gitHubAppUrl: string;
  accounts: AccountInfo[];
  /**
   * The server's stored GitHub token is dead and silent refresh failed — only
   * the "Reconnect GitHub" browser round-trip can fix it. While set, accounts
   * is degraded to the user's own account.
   */
  gitHubReauthRequired?: boolean;
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
