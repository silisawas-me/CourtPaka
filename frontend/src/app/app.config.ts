import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { catchError, of } from 'rxjs';
import { AuthService } from './core/auth/auth.service';
import { apiErrorInterceptor } from './core/http/api-error';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Route parameters arrive as component inputs, so pages do not read route snapshots.
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor])),
    // A session cookie may already exist; resolve it before the first route renders so guards and
    // pages never have to distinguish "signed out" from "not loaded yet". A failure here must never
    // stop the app from booting: a rejected initializer renders nothing at all.
    provideAppInitializer(() =>
      inject(AuthService)
        .loadCurrentUser()
        .pipe(catchError(() => of(null))),
    ),
  ],
};
