import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, map, Observable, of, shareReplay, switchMap, tap } from 'rxjs';
import { ApiError } from '../http/api-error';
import { Language } from '../i18n/locales';
import { TranslationService } from '../i18n/translation.service';

export interface CurrentUser {
  id: string;
  email: string;
  emailConfirmed: boolean;
  language: string;
}

export interface RegisterInput {
  email: string;
  password: string;
  privacyPolicyVersion: string;
  language: Language;
  phoneNumber: string | null;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly translations = inject(TranslationService);

  private readonly user = signal<CurrentUser | null>(null);
  private readonly loaded = signal(false);

  /** The signed-in account, or null. Guards should wait for {@link ready} before trusting it. */
  readonly currentUser = this.user.asReadonly();
  readonly ready = this.loaded.asReadonly();

  /** Static server config; fetched once per app load and shared by every caller. */
  private readonly policyVersion$ = this.http
    .get<{ version: string }>('/api/auth/privacy-policy')
    .pipe(
      map((response) => response.version),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

  privacyPolicyVersion(): Observable<string> {
    return this.policyVersion$;
  }

  register(input: RegisterInput): Observable<void> {
    return this.http.post<void>('/api/auth/register', input);
  }

  login(email: string, password: string): Observable<CurrentUser | null> {
    return this.http.post<void>('/api/auth/login', { email, password }).pipe(
      // The session cookie arrives with the login response; read the account it belongs to.
      switchMap(() => this.loadCurrentUser()),
    );
  }

  loadCurrentUser(): Observable<CurrentUser | null> {
    return this.http.get<CurrentUser>('/api/auth/me').pipe(
      tap((user) => this.adopt(user)),
      catchError((error: unknown) => {
        if (error instanceof ApiError && error.status === 401) {
          this.adopt(null);
          return of(null);
        }
        throw error;
      }),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {}).pipe(tap(() => this.adopt(null)));
  }

  verifyEmail(userId: string, token: string): Observable<void> {
    return this.http.post<void>('/api/auth/verify-email', { userId, token });
  }

  resendVerification(email: string): Observable<void> {
    return this.http.post<void>('/api/auth/resend-verification', { email });
  }

  changeLanguage(language: Language): Observable<void> {
    return this.http.put<void>('/api/auth/me/language', { language }).pipe(
      tap(() => {
        const user = this.user();
        if (user) {
          this.user.set({ ...user, language });
        }
      }),
    );
  }

  private adopt(user: CurrentUser | null): void {
    this.user.set(user);
    this.loaded.set(true);
    if (user) {
      this.translations.useAccountLanguage(user.language);
    }
  }
}
