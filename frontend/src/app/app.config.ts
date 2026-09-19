import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { apiErrorInterceptor } from './core/http/api-error';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor])),
    // A session cookie may already exist; resolve it before the first route renders so guards and
    // pages never have to distinguish "signed out" from "not loaded yet".
    provideAppInitializer(() => inject(AuthService).loadCurrentUser()),
  ],
};
