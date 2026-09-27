import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  isDevMode,
} from '@angular/core';
import type { ActivatedRouteSnapshot } from '@angular/router';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
  withViewTransitions,
} from '@angular/router';
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
/**
 * Whether a navigation is somebody coming in off the front page — the only one drawn as a
 * movement rather than a new screen, because the court is on both sides of it.
 *
 * Asked with the two routes' own paths rather than their addresses: the router hands this a pair
 * of `ActivatedRouteSnapshot`, whose `url` is a list of segments and not a string, and reading it
 * as one made every navigation look like somebody else's and skipped them all.
 */
export function pathOf(route: ActivatedRouteSnapshot): string | undefined {
  // The router hands over the root of each tree, whose own `routeConfig` is null; the page is at
  // the bottom of it.
  let leaf = route;
  while (leaf.firstChild) {
    leaf = leaf.firstChild;
  }
  return leaf.routeConfig?.path;
}

export function isTheWayIn(from: string | undefined, to: string | undefined): boolean {
  const doors = ['login', 'register'];
  const front = '';
  return (
    (from === front && doors.includes(to ?? '')) || (doors.includes(from ?? '') && to === front)
  );
}

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
      // The way in is one movement, not three screens: the court on the front page is the court
      // behind the sign-in form, so choosing a door carries it across rather than blinking to a
      // new page. Every other navigation is told to skip — a view transition snapshots the whole
      // document, and the court grid is the page PRD 8 measures (`grid_lcp.py`). Restricted here
      // rather than per-page because the router is the only place that knows both ends of a
      // navigation, and both ends are what decide whether this is that movement.
      withViewTransitions({
        skipInitialTransition: true,
        onViewTransitionCreated: ({ transition, from, to }) => {
          if (!isTheWayIn(pathOf(from), pathOf(to))) {
            transition.skipTransition();
          }
        },
      }),
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
