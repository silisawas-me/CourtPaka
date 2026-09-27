import { Component, computed, inject, input } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { catchError, filter, map, merge, of, startWith, switchMap } from 'rxjs';
import { TranslationService } from '../core/i18n/translation.service';
import { Venue, VenueService } from '../core/venues/venue.service';
import { ALL_VENUES, VENUE_OTHER, VENUE_SECTIONS, VenueLink, waitingOn } from './venue-nav';

/** A door with its route already built, its count read, and whether it is the page on screen. */
interface Door {
  readonly path: unknown[];
  readonly fragment: string | undefined;
  readonly label: string;
  readonly testId: string;
  /** The same door, named for the bar it is in, so a script says which one it pressed. */
  readonly tabTestId: string;
  readonly waiting: number | null;
  readonly on: boolean;
}

/** One venue in the switch along the top bar, or "every venue". */
interface Pill {
  readonly path: unknown[];
  readonly label: string;
  readonly testId: string;
  readonly on: boolean;
}

/** Where the router is: the venue's page segment (`bookings`, `now`, …) and any fragment. */
interface Here {
  readonly segment: string | null;
  readonly fragment: string | null;
}

/**
 * The owner app's frame (docs/plan/owner-app.md): the dark rail with the four sections and the
 * rest of a venue's pages under them, and the bar along the top with the page's name, the venue
 * switch and the day. On a phone the rail becomes the tabs along the bottom.
 *
 * Its own component rather than part of the app shell because every page of the app pays for what
 * the root component carries, and the front door and sign-in have no counter, no shift and no
 * venue.
 */
@Component({
  imports: [RouterLink],
  selector: 'app-venue-shell',
  styleUrl: './venue-shell.scss',
  templateUrl: './venue-shell.html',
})
export class VenueShell {
  private readonly venues = inject(VenueService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);

  /** The venue on screen, or `ALL_VENUES` on the overview of every venue. */
  readonly venueId = input.required<string>();

  /** Every venue this person works at, with their role at each — the server's, not guessed. */
  private readonly mine = toSignal(this.venues.mine().pipe(catchError(() => of([] as Venue[]))), {
    initialValue: [] as Venue[],
  });

  private readonly here = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      startWith(null),
      map(() => hereIn(this.router.url)),
    ),
    { initialValue: hereIn(this.router.url) },
  );

  protected readonly current = computed(
    () => this.mine().find((venue) => venue.id === this.venueId()) ?? null,
  );

  protected readonly onAll = computed(() => this.venueId() === ALL_VENUES);

  /** At the venue on screen, or — on the overview — at any venue they have. */
  private readonly isOwner = computed(() => {
    const current = this.current();
    return current ? current.role === 'Owner' : this.mine().some((venue) => venue.role === 'Owner');
  });

  /**
   * What is waiting at this venue (PRD US-17), read when the shell arrives and again on every
   * move between its pages — which is when the number can have changed. None on the overview:
   * the counts belong to one venue. `switchMap` drops the answer for the venue that was left.
   */
  private readonly attention = toSignal(
    merge(
      toObservable(this.venueId),
      this.router.events.pipe(
        filter((event) => event instanceof NavigationEnd),
        map(() => this.venueId()),
      ),
    ).pipe(
      switchMap((venueId) =>
        venueId === ALL_VENUES
          ? of(null)
          : this.venues.attention(venueId).pipe(catchError(() => of(null))),
      ),
      takeUntilDestroyed(),
    ),
    { initialValue: null },
  );

  /**
   * The four sections. On the overview the schedule is every venue's, and the others open at
   * the first venue they make sense for — a section with nowhere to open is not drawn.
   */
  protected readonly sections = computed<Door[]>(() => {
    const here = this.here();
    const onAll = this.onAll();

    return VENUE_SECTIONS.flatMap((link) => {
      if (link.ownerOnly && !this.isOwner()) {
        return [];
      }

      if (onAll && link.testId === 'nav-schedule') {
        return [this.door(link, ['/venues'], true)];
      }

      const venueId = onAll ? this.firstFor(link)?.id : this.venueId();
      return venueId
        ? [this.door(link, ['/venues', venueId, ...link.to], !onAll && isAt(link, here))]
        : [];
    });
  });

  /** The rest of a venue's pages. Only at a venue: each is one venue's, never every venue's. */
  protected readonly others = computed<Door[]>(() => {
    const venueId = this.venueId();
    if (this.onAll()) {
      return [];
    }

    const here = this.here();
    return VENUE_OTHER.map((link) =>
      this.door(link, ['/venues', venueId, ...link.to], isAt(link, here)),
    );
  });

  /** The phone's tabs: the sections, then the venue's own page for everything else. */
  protected readonly tabs = computed<Door[]>(() => {
    const venue = this.others().find((door) => door.testId === 'nav-venue');
    return [...this.sections(), ...(venue ? [venue] : [])].slice(0, 5);
  });

  /** The name of the page on screen, which is what the top bar says first. */
  protected readonly title = computed(() => {
    const on = [...this.sections(), ...this.others()].find((door) => door.on);
    return on?.label ?? 'nav.section.schedule';
  });

  /**
   * The venue switch. "Every venue" only on the schedule (O10). Switching keeps the page: the
   * timeline of one venue becomes the timeline of the other.
   */
  protected readonly pills = computed<Pill[]>(() => {
    const here = this.here();
    const onSchedule = this.onAll() || here.segment === 'bookings' || here.segment === 'now';
    const segment = this.onAll() ? 'bookings' : (here.segment ?? '');

    const venues = this.mine().map((venue) => ({
      path: ['/venues', venue.id, ...(segment ? [segment] : [])],
      label: venue.name,
      testId: `pill-${venue.id}`,
      on: venue.id === this.venueId(),
    }));

    return onSchedule && this.mine().length > 1
      ? [
          {
            path: ['/venues'],
            label: 'nav.allVenues',
            testId: 'pill-all',
            on: this.onAll(),
          },
          ...venues,
        ]
      : venues;
  });

  /** The timeline or the board of right now: two ways of reading the same schedule (2a / 2b). */
  protected readonly views = computed(() => {
    const segment = this.here().segment;
    if (this.onAll() || (segment !== 'bookings' && segment !== 'now')) {
      return [];
    }

    return (['bookings', 'now'] as const).map((view) => ({
      path: ['/venues', this.venueId(), view],
      label: view === 'bookings' ? 'nav.view.timeline' : 'nav.view.now',
      testId: `view-${view}`,
      on: segment === view,
    }));
  });

  private firstFor(link: VenueLink): Venue | undefined {
    return this.mine().find((venue) => !link.ownerOnly || venue.role === 'Owner');
  }

  private door(link: VenueLink, path: unknown[], on: boolean): Door {
    return {
      path,
      fragment: link.fragment,
      label: link.label,
      testId: link.testId,
      tabTestId: link.testId.replace('nav-', 'tab-'),
      waiting: waitingOn(link, this.attention()),
      on,
    };
  }
}

/** The venue page segment and fragment of a URL like `/venues/{id}/settings#prices`. */
function hereIn(url: string): Here {
  const [path, fragment] = url.split('#');
  const segments = path.split('?')[0].split('/').filter(Boolean);
  return {
    segment: segments[0] === 'venues' && segments.length >= 2 ? (segments[2] ?? '') : null,
    fragment: fragment ?? null,
  };
}

/**
 * Whether a door is the page on screen. Asked of the segment and the fragment rather than left to
 * `routerLinkActive`, because prices and the rest of settings are one page told apart by a
 * fragment, and the timeline and "now" are one section.
 */
function isAt(link: VenueLink, here: Here): boolean {
  const segment = link.to[0] ?? '';
  const pages = [segment, ...(link.alsoAt ?? []).map((one) => one[0])];
  if (here.segment === null || !pages.includes(here.segment)) {
    return false;
  }

  if (link.fragment) {
    return here.fragment === link.fragment;
  }

  // The rest of settings is everything on that page but the part a section has claimed.
  const claimed = VENUE_SECTIONS.some(
    (section) =>
      section.fragment && section.to[0] === segment && section.fragment === here.fragment,
  );
  return !claimed;
}
