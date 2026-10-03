import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { catchError, filter, map, of, startWith } from 'rxjs';
import { fromPlainDate, venueNow } from '../core/i18n/plain-date';
import { TranslationService } from '../core/i18n/translation.service';
import { Venue, VenuePermission, VenueService } from '../core/venues/venue.service';
import { ALL_VENUES, VENUE_SECTIONS, VenueLink } from './venue-nav';

import { WalkIn } from '../features/venues/walk-in';
import { WalkInEvents } from '../core/venues/walk-in.events';
/** A door with its route already built, and whether it is the page on screen. */
interface Door {
  readonly path: unknown[];
  readonly fragment: string | undefined;
  readonly label: string;
  readonly testId: string;
  /** The same door, named for the bar it is in, so a script says which one it pressed. */
  readonly tabTestId: string;
  readonly on: boolean;
}

/**
 * The schedule's views, left to right: the tracks, the floor this minute, the list, and the slips
 * waiting (thai-fit T5) — the last only for who may check them.
 */
const SCHEDULE_VIEWS = ['timeline', 'now', 'bookings', 'slips'] as const;

/** The pages whose sections are tabs on the top bar, and what their tabs are called. */
const SECTION_TABS: Record<string, { tabs: readonly string[]; label: string; testId: string }> = {
  dashboard: { tabs: ['overview', 'close'], label: 'revenue.tab', testId: 'revenue-tab' },
  pricing: {
    tabs: ['prices', 'hours', 'courts', 'staff', 'catalog', 'policy'],
    label: 'pricing.tab',
    testId: 'pricing-tab',
  },
};

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
  readonly tab: string | null;
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
  imports: [RouterLink, WalkIn],
  selector: 'app-venue-shell',
  styleUrl: './venue-shell.scss',
  templateUrl: './venue-shell.html',
})
export class VenueShell {
  private readonly venues = inject(VenueService);
  private readonly router = inject(Router);

  constructor() {
    // A page asked for the walk-in on a court and hour (a tapped empty cell on the timeline).
    inject(WalkInEvents)
      .open.pipe(takeUntilDestroyed())
      .subscribe(({ venueId, courtId, hour }) => {
        if (venueId === this.walkInAt()) {
          this.walkInCell.set({ courtId, hour });
          this.walkIn.set(true);
        }
      });
  }

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

  /**
   * The day beside the branches on the overview, as the design writes it ("พุธ 30 ก.ย."): the
   * overview is always today, so the day is said rather than picked.
   */
  protected readonly todayLabel = computed(() => {
    // The venue's day: at 01:00 a venue open until 02:00 is still on yesterday (thai-fit T4).
    const starts = this.current()?.dayStartsHour ?? 0;
    const day = fromPlainDate(venueNow(new Date(), starts).date)!;
    const date = new Intl.DateTimeFormat(this.i18n.locale(), { day: 'numeric', month: 'short' });
    return `${this.i18n.t(`overview.weekday.${day.getDay()}`)} ${date.format(day)}`;
  });

  /** Where "สแกน QR" goes: the board of right now of this venue, or of the first on the overview. */
  protected readonly scanAt = computed(() => {
    const id = this.onAll() ? this.mine()[0]?.id : this.venueId();
    return id ? ['/venues', id, 'now'] : null;
  });

  /** The venue a walk-in is sold at: this one, or on the overview the first branch (the design's). */
  protected readonly walkInAt = computed(() =>
    this.onAll() ? (this.mine()[0]?.id ?? null) : this.venueId(),
  );

  /** The walk-in modal is open (owner app PR-3). */
  protected readonly walkIn = signal(false);
  /** Where the walk-in opens when a page asked for a court and hour (a tapped empty cell). */
  protected readonly walkInCell = signal<{ courtId: string; hour: number } | null>(null);

  protected openWalkIn(): void {
    this.walkInCell.set(null);
    this.walkIn.set(true);
  }

  /** At the venue on screen, or — on the overview — at any venue they have. */
  private readonly isOwner = computed(() => {
    const current = this.current();
    return current ? current.role === 'Owner' : this.mine().some((venue) => venue.role === 'Owner');
  });

  /**
   * The four sections. On the overview the schedule is every venue's, and the others open at
   * the first venue they make sense for — a section with nowhere to open is not drawn.
   */
  protected readonly sections = computed<Door[]>(() => {
    const here = this.here();
    const onAll = this.onAll();

    return VENUE_SECTIONS.flatMap((link) => {
      if ((link.ownerOnly && !this.isOwner()) || (link.needs && !this.holds(link.needs))) {
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

  /** The name of the page on screen, which is what the top bar says first. */
  protected readonly title = computed(() => {
    const on = this.sections().find((door) => door.on);
    return on?.label ?? 'nav.section.schedule';
  });

  /**
   * The venue switch. "Every venue" only on the schedule (O10). Switching keeps the page: the
   * timeline of one venue becomes the timeline of the other.
   */
  protected readonly pills = computed<Pill[]>(() => {
    const here = this.here();
    const onSchedule = this.onAll() || SCHEDULE_VIEWS.some((view) => view === here.segment);
    const segment = this.onAll() ? 'timeline' : (here.segment ?? '');

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

  /** The timeline, the board of right now, the list and the slips: one schedule, four readings. */
  protected readonly views = computed(() => {
    const segment = this.here().segment;
    if (this.onAll() || !SCHEDULE_VIEWS.some((view) => view === segment)) {
      return [];
    }

    // A slip is somebody's bank account: the view is for who holds VerifySlip (PDPA), as the
    // server's queue is.
    const venue = this.current();
    const checksSlips = venue ? mayAt(venue, 'VerifySlip') : false;
    return SCHEDULE_VIEWS.filter((view) => view !== 'slips' || checksSlips).map((view) => ({
      path: ['/venues', this.venueId(), view],
      label: `nav.view.${view}`,
      testId: `view-${view}`,
      on: segment === view,
    }));
  });

  /**
   * The tabs of revenue and of prices & setup, on the top bar as the artboards draw them (beside
   * the branches, like the schedule's views). Each is `?tab=` on its page, so a tab is a link.
   */
  protected readonly tabs = computed(() => {
    const { segment, tab } = this.here();
    const set = segment === null || this.onAll() ? undefined : SECTION_TABS[segment];
    if (!set) {
      return [];
    }
    const owner = this.current()?.role === 'Owner';
    const shown = set.tabs.filter((one) => one !== 'staff' || owner);
    const on = tab && shown.includes(tab) ? tab : shown[0];
    return shown.map((one) => ({
      path: ['/venues', this.venueId(), segment],
      query: { tab: one === shown[0] ? null : one },
      label: `${set.label}.${one}`,
      testId: `${set.testId}-${one}`,
      on: one === on,
    }));
  });

  /** The tab a branch switch keeps: the drawer of one branch becomes the drawer of the other. */
  protected readonly keptTab = computed(() => {
    const tab = this.here().tab;
    return tab ? { tab } : {};
  });

  private firstFor(link: VenueLink): Venue | undefined {
    return this.mine().find(
      (venue) =>
        (!link.ownerOnly || venue.role === 'Owner') && (!link.needs || mayAt(venue, link.needs)),
    );
  }

  /** Whether they hold a permission at the venue on screen, or — on the overview — at any. */
  private holds(permission: VenuePermission): boolean {
    const current = this.current();
    return current
      ? mayAt(current, permission)
      : this.mine().some((venue) => mayAt(venue, permission));
  }

  private door(link: VenueLink, path: unknown[], on: boolean): Door {
    return {
      path,
      fragment: link.fragment,
      label: link.label,
      testId: link.testId,
      tabTestId: link.testId.replace('nav-', 'tab-'),
      on,
    };
  }
}

/** The venue page segment and fragment of a URL like `/venues/{id}/settings#prices`. */
function hereIn(url: string): Here {
  const [path, fragment] = url.split('#');
  const [route, query] = path.split('?');
  const segments = route.split('/').filter(Boolean);
  return {
    segment: segments[0] === 'venues' && segments.length >= 2 ? (segments[2] ?? '') : null,
    fragment: fragment ?? null,
    tab: new URLSearchParams(query ?? '').get('tab'),
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

/** An Owner holds every permission; anyone else holds what the venue gave them. */
function mayAt(venue: Venue, permission: VenuePermission): boolean {
  return venue.role === 'Owner' || venue.permissions.includes(permission);
}
