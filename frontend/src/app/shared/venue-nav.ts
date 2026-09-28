import { VenueAttention } from '../core/venues/venue.service';

/** The shell stands on "every venue" rather than on one: the owner's overview (badPaka Owner App). */
export const ALL_VENUES = 'all';

/** One door in the venue's own navigation. */
export interface VenueLink {
  /** Path segments under `/venues/{venueId}`, empty for the venue itself. */
  readonly to: readonly string[];
  readonly label: string;
  readonly testId: string;
  /** A fragment on the page it opens, for a section that is a part of a longer page. */
  readonly fragment?: string;
  /** Which count, if any, belongs beside it (PRD US-17). */
  readonly waiting?: 'slips' | 'money';
  /** Drawn only for somebody who owns the venue: money and prices are the owner's (US-14). */
  readonly ownerOnly?: boolean;
  /** Also where a page further down this list is — the timeline and "now" are both the schedule. */
  readonly alsoAt?: readonly (readonly string[])[];
}

/**
 * The four sections of the owner app, as the design draws them (docs/plan/owner-app.md): the
 * court schedule, prices, revenue, and members. Staff see the schedule and members — the other
 * two are the owner's money and the owner's prices.
 *
 * The schedule is the one section that can stand on every venue at once; the others are always
 * about one of them (O10).
 */
export const VENUE_SECTIONS: readonly VenueLink[] = [
  {
    to: ['timeline'],
    label: 'nav.section.schedule',
    testId: 'nav-schedule',
    alsoAt: [['now']],
  },
  { to: ['pricing'], label: 'nav.section.pricing', testId: 'nav-pricing', ownerOnly: true },
  { to: ['dashboard'], label: 'nav.section.revenue', testId: 'nav-dashboard', ownerOnly: true },
  { to: ['packages'], label: 'nav.section.members', testId: 'nav-packages' },
];

/**
 * Everything else a venue has, which the design's four sections do not name. Kept, in a quieter
 * group, because each is work somebody does (the owner's ruling, docs/plan/owner-app.md).
 */
export const VENUE_OTHER: readonly VenueLink[] = [
  // The day's list with every door the timeline's panel does not carry (cancel, no-show, refunds).
  { to: ['bookings'], label: 'nav.dayBookings', testId: 'nav-bookings' },
  { to: ['slip-queue'], label: 'slipQueue.title', testId: 'nav-slip-queue', waiting: 'slips' },
  { to: ['money'], label: 'money.title', testId: 'nav-money', waiting: 'money' },
  { to: ['series'], label: 'series.title', testId: 'nav-series' },
  { to: ['shop'], label: 'shop.title', testId: 'nav-shop' },
  { to: ['closures'], label: 'closures.title', testId: 'nav-closures' },
  { to: ['settings'], label: 'settings.title', testId: 'nav-settings' },
  { to: [], label: 'venues.detail', testId: 'nav-venue' },
];

/** The count that belongs beside a door, or null when there is nothing waiting behind it. */
export function waitingOn(link: VenueLink, attention: VenueAttention | null): number | null {
  if (!attention || !link.waiting) {
    return null;
  }

  const count =
    link.waiting === 'slips' ? attention.slipsToCheck : attention.bookingsWithMoneyWaiting;

  return count > 0 ? count : null;
}
