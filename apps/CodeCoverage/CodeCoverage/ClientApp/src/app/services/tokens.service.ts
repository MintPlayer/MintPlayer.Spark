import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface TokenInfo {
  id: string;
  accountLogin: string;
  description?: string;
  scope: 'Account' | 'Repository';
  repositoryFullName?: string;
  createdAtUtc: string;
  revokedAtUtc?: string;
}

export interface CreatedToken {
  tokenValue: string;
  accountLogin: string;
  description?: string;
  scope: 'Account' | 'Repository';
  repositoryFullName?: string;
}

@Injectable({ providedIn: 'root' })
export class TokensService {
  private readonly http = inject(HttpClient);

  list(account: string): Promise<TokenInfo[]> {
    const params = new HttpParams().set('account', account);
    return firstValueFrom(this.http.get<TokenInfo[]>('/api/tokens', { params }));
  }

  create(accountLogin: string, description: string | null, repositoryFullName: string | null): Promise<CreatedToken> {
    return firstValueFrom(this.http.post<CreatedToken>('/api/tokens', {
      accountLogin,
      description,
      scope: repositoryFullName ? 'Repository' : 'Account',
      repositoryFullName,
    }));
  }

  revoke(id: string): Promise<void> {
    // TokenInfo.Id is the document id "ApiTokens/{hash}"; the route wants the hash.
    const hash = id.split('/').pop()!;
    return firstValueFrom(this.http.delete<void>(`/api/tokens/${encodeURIComponent(hash)}`)).then(() => undefined);
  }
}
