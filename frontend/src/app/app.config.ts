import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  isDevMode,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { apiErrorInterceptor } from './core/http/api-error';
import { routes } from './app.routes';
import { provideServiceWorker } from '@angular/service-worker';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Route parameters arrive as component inputs, so pages do not read route snapshots.
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor])),
    // A session cookie may already exist. Its answer is asked for at boot but not waited for:
    // waiting held every page — the public court grid too — behind a round trip, which the grid's
    // LCP target (PRD 8) cannot afford. Guarded pages wait for it themselves (authGuard).
    provideAppInitializer(() => inject(AuthService).lookForSession()),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
