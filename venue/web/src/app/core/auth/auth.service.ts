import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, map, Observable, of, shareReplay, switchMap, tap } from 'rxjs';
import { ApiError, SESSION_NOT_ESTABLISHED_CODE } from '../http/api-error';
import { Language } from '../i18n/locales';
import { TranslationService } from '../i18n/translation.service';

export interface CurrentUser {
  id: string;
  /** Null for a LINE account that shared no address (PRD US-01). */
  email: string | null;
  emailConfirmed: boolean;
  language: string;
  /**
   * Whether this person acts for the platform (PRD US-20). Used to decide which doors to draw,
   * never to decide anything: every one of those doors asks the server again.
   */
  isPlatformAdmin: boolean;
  phoneNumber: string | null;
  /** The name the counter knows them by, where they have one. */
  displayName?: string | null;
  /** Signs in with a six-digit passcode an owner set (thai-fit T1). */
  usesPasscode?: boolean;
  /** Has not accepted the privacy policy yet: the first sign-in asks before anything else. */
  needsConsent?: boolean;
  invitationId?: string;
  invitationToken?: string;
  /** False for a LINE account: deleting it is confirmed at LINE instead of with a password. */
  hasPassword: boolean;
  signsInWithLine: boolean;
  /** What the account still needs before it can book, decided by the server, or null. */
  cannotBookBecause: string | null;
}

/** Who LINE said came back, waiting for the policy to be accepted (PRD US-01). */
export interface LinePendingSignUp {
  name: string | null;
  email: string | null;
}

export interface RegisterInput {
  /** Optional when signing up from a staff invitation link with a phone (thai-fit T1). */
  email: string | null;
  password: string;
  privacyPolicyVersion: string;
  language: Language;
  phoneNumber: string | null;
  invitationId?: string;
  invitationToken?: string;
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

  /**
   * The first look at the session cookie, started when the app boots and shared by whoever asks
   * before it answers. The app does not wait for it to render: a public page (the court grid,
   * PRD 8's LCP target) would otherwise pay a round trip to learn nothing it needs. Pages that do
   * need it wait through {@link whenReady}; a failure reads as signed out rather than as no app.
   */
  private readonly firstLook$ = this.http.get<CurrentUser>('/api/auth/me').pipe(
    map((user): CurrentUser | null => user),
    catchError(() => of(null)),
    tap((user) => {
      // A sign-in or sign-out that finished first knows better than this older answer.
      if (!this.loaded()) {
        this.adopt(user);
      }
    }),
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  /** Starts the first look without waiting for it. Called once, as the app boots. */
  lookForSession(): void {
    this.firstLook$.subscribe();
  }

  /** The signed-in account once the first look has answered (at once if it already has). */
  whenReady(): Observable<CurrentUser | null> {
    return this.loaded() ? of(this.user()) : this.firstLook$.pipe(map(() => this.user()));
  }

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

  login(email: string, password: string): Observable<CurrentUser> {
    return this.http.post<void>('/api/auth/login', { email, password }).pipe(
      // The session cookie arrives with the login response; read the account it belongs to.
      switchMap(() => this.loadCurrentUser()),
      map((user) => {
        if (!user) {
          // The password was right but the session did not survive the round trip (a rejected
          // cookie, a revoked session). Reporting success here would look like a dead button.
          throw new ApiError(SESSION_NOT_ESTABLISHED_CODE, 0);
        }
        return user;
      }),
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

  /**
   * Clears this browser either way — leaving the UI signed in would trap the user — but reports
   * whether the server confirmed it. An unconfirmed sign-out leaves the cookie alive on the
   * device, which matters on a shared computer.
   */
  logout(): Observable<{ confirmed: boolean }> {
    return this.http.post<void>('/api/auth/logout', {}).pipe(
      map(() => ({ confirmed: true })),
      // A 401 means the session was already gone, which is the outcome the user asked for.
      catchError((error: unknown) =>
        of({ confirmed: error instanceof ApiError && error.status === 401 }),
      ),
      tap(() => this.adopt(null)),
    );
  }

  /**
   * Asks the server to forget this account (PDPA, PRD 8). The server signs the session out
   * itself; this browser forgets the user once it has.
   */
  deleteAccount(password: string | null): Observable<void> {
    return this.http
      .post<void>('/api/auth/me/delete', { password })
      .pipe(tap(() => this.adopt(null)));
  }

  verifyEmail(userId: string, token: string): Observable<void> {
    return this.http.post<void>('/api/auth/verify-email', { userId, token });
  }

  resendVerification(email: string): Observable<void> {
    return this.http.post<void>('/api/auth/resend-verification', { email });
  }

  /** Whether this deployment has a LINE channel; the sign-in screens ask before offering it. */
  private readonly lineEnabled$ = this.http.get<{ enabled: boolean }>('/api/auth/line').pipe(
    map((response) => response.enabled),
    catchError(() => of(false)),
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  lineEnabled(): Observable<boolean> {
    return this.lineEnabled$;
  }

  linePending(): Observable<LinePendingSignUp> {
    return this.http.get<LinePendingSignUp>('/api/auth/line/pending');
  }

  /** Accepts the policy and makes the account LINE's answer is waiting for (PRD US-01). */
  completeLineSignUp(input: {
    privacyPolicyVersion: string;
    language: Language;
    phoneNumber: string | null;
  }): Observable<CurrentUser | null> {
    return this.http
      .post<void>('/api/auth/line/complete', input)
      .pipe(switchMap(() => this.loadCurrentUser()));
  }

  /** The privacy policy, accepted by the person themselves (PDPA); the server owns the version. */
  consent(): Observable<CurrentUser | null> {
    return this.privacyPolicyVersion().pipe(
      switchMap((privacyPolicyVersion) =>
        this.http.post<void>('/api/auth/me/consent', { privacyPolicyVersion }),
      ),
      switchMap(() => this.loadCurrentUser()),
    );
  }

  /** A passcode account's own new passcode, given the one it has now. */
  changePasscode(current: string, next: string): Observable<void> {
    return this.http.post<void>('/api/auth/me/passcode', { current, new: next });
  }

  /** The number a venue reaches the booker on (PRD US-01); empty clears it. */
  changePhone(phoneNumber: string | null): Observable<void> {
    return this.http.put<void>('/api/auth/me/phone', { phoneNumber }).pipe(
      switchMap(() => this.loadCurrentUser()),
      map(() => undefined),
    );
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
