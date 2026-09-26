import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  isDevMode,
} from '@angular/core';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { DEFAULT_LANGUAGE } from './core/i18n/locales';
import { TranslationService } from './core/i18n/translation.service';
import { PublicVenueService } from './core/venues/public-venue.service';
import { plainDate, venueToday } from './core/i18n/plain-date';
import { apiErrorInterceptor } from './core/http/api-error';
import { routes } from './app.routes';
import { provideServiceWorker } from '@angular/service-worker';

/**
 * The venue and day of a grid address (`/book/{venueId}?date=`), or null for any other page. The
 * day the page will ask for is the day in the address, or today at the venue — the same rule the
 * page itself uses, because a different answer here would be a wasted request.
 */
export function gridInTheAddress(
  path: string,
  search: string,
): { venueId: string; date: string } | null {
  const venueId = /^\/book\/([^/?#]+)\/?$/.exec(path)?.[1];
  if (!venueId) {
    return null;
  }

  const date = new URLSearchParams(search).get('date');
  return { venueId: decodeURIComponent(venueId), date: date ?? plainDate(venueToday()) };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Route parameters arrive as component inputs, so pages do not read route snapshots.
    // A URL with a fragment scrolls to it, which is what makes the way-finding on a long page of
    // settings work and what makes such a link worth sending to somebody.
    provideRouter(
      routes,
      withComponentInputBinding(),
      withInMemoryScrolling({ anchorScrolling: 'enabled' }),
    ),
    provideHttpClient(withFetch(), withInterceptors([apiErrorInterceptor])),
    // A session cookie may already exist. Its answer is asked for at boot but not waited for:
    // waiting held every page — the public court grid too — behind a round trip, which the grid's
    // LCP target (PRD 8) cannot afford. Guarded pages wait for it themselves (authGuard).
    // A visitor who reads English chose that last time; the words are fetched before the first
    // paint so they never read a screen of Thai first. A Thai reader waits for nothing: those
    // words ship with the app (PRD US-23).
    provideAppInitializer(() => {
      const i18n = inject(TranslationService);
      // Never a reason for nothing to render: an initializer that rejects fails the whole
      // bootstrap, and Thai is already here to read in the meantime.
      return i18n.language() === DEFAULT_LANGUAGE ? undefined : i18n.load(i18n.language());
    }),
    provideAppInitializer(() => inject(AuthService).lookForSession()),
    // A booker who opened a grid link is waiting for one thing: that day. Asking for it here
    // starts it beside the download of the page that draws it, rather than after it (PRD 8 LCP).
    provideAppInitializer(() => {
      const asked = gridInTheAddress(location.pathname, location.search);
      if (asked) {
        inject(PublicVenueService).prefetch(asked.venueId, asked.date);
      }
    }),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
