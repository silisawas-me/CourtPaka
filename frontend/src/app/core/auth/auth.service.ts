import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, map, Observable, of, switchMap, tap, throwError } from 'rxjs';
import { Language } from '../i18n/locales';

export interface CurrentUser {
  id: string;
  email: string;
  emailConfirmed: boolean;
  language: Language;
}

export interface RegisterInput {
  email: string;
  password: string;
  privacyPolicyVersion: string;
  language: Language;
  phoneNumber: string | null;
}

/** Error codes come from the API (PRD US-23); the UI turns them into text. */
export class ApiError extends Error {
  constructor(readonly code: string) {
    super(code);
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  private readonly user = signal<CurrentUser | null>(null);

  readonly currentUser = this.user.asReadonly();

  privacyPolicyVersion(): Observable<string> {
    return this.http
      .get<{ version: string }>('/api/auth/privacy-policy')
      .pipe(map((response) => response.version));
  }

  register(input: RegisterInput): Observable<void> {
    return this.http.post<void>('/api/auth/register', input).pipe(catchError(toApiError));
  }

  login(email: string, password: string): Observable<CurrentUser | null> {
    return this.http.post<void>('/api/auth/login', { email, password }).pipe(
      catchError(toApiError),
      // The session cookie arrives with the login response; read the account it belongs to.
      switchMap(() => this.loadCurrentUser()),
    );
  }

  loadCurrentUser(): Observable<CurrentUser | null> {
    return this.http.get<CurrentUser>('/api/auth/me').pipe(
      tap((user) => this.user.set(user)),
      catchError((error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.user.set(null);
          return of(null);
        }
        return toApiError(error);
      }),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {}).pipe(tap(() => this.user.set(null)));
  }

  verifyEmail(userId: string, token: string): Observable<void> {
    return this.http
      .post<void>('/api/auth/verify-email', { userId, token })
      .pipe(catchError(toApiError));
  }

  resendVerification(email: string): Observable<void> {
    return this.http
      .post<void>('/api/auth/resend-verification', { email })
      .pipe(catchError(toApiError));
  }

  changeLanguage(language: Language): Observable<void> {
    return this.http.put<void>('/api/auth/me/language', { language }).pipe(catchError(toApiError));
  }
}

function toApiError(error: HttpErrorResponse): Observable<never> {
  if (error.status === 429) {
    return throwError(() => new ApiError('tooManyRequests'));
  }

  const code = (error.error as { code?: string } | null)?.code;
  return throwError(() => new ApiError(code ?? 'unknown'));
}
