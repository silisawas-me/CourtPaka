import { VenueAttention } from '../core/venues/venue.service';

/** One door in the venue's own navigation. */
export interface VenueLink {
  /** Path segments under `/venues/{venueId}`, empty for the venue itself. */
  readonly to: readonly string[];
  readonly label: string;
  readonly testId: string;
  /**
   * The label a phone's bottom bar uses, and the mark that the door belongs there. A door
   * without one is reached from the venue's own page, which is the last tab.
   */
  readonly short?: string;
  /** Which count, if any, belongs beside it (PRD US-17). */
  readonly waiting?: 'slips' | 'money';
}

/**
 * What a venue's own navigation holds, split the way a day is: what is happening at the counter
 * now, and what is set up behind it (PRD US-25's console, and the doors around it).
 *
 * One list rather than markup, so the sidebar on a desk and the bar along the bottom of a phone
 * are the same doors leading to the same places — adding one is a line here, not two places that
 * drift apart.
 */
export const VENUE_WORK: readonly VenueLink[] = [
  {
    to: ['bookings'],
    label: 'venueBookings.title',
    short: 'nav.tab.today',
    testId: 'nav-venue-bookings',
  },
  {
    to: ['slip-queue'],
    label: 'slipQueue.title',
    short: 'nav.tab.slips',
    testId: 'nav-slip-queue',
    waiting: 'slips',
  },
  {
    to: ['money'],
    label: 'money.title',
    short: 'nav.tab.money',
    testId: 'nav-money',
    waiting: 'money',
  },
];

export const VENUE_BACK_OF_HOUSE: readonly VenueLink[] = [
  {
    to: ['dashboard'],
    label: 'dashboard.title',
    short: 'nav.tab.reports',
    testId: 'nav-dashboard',
  },
  { to: ['settings'], label: 'settings.title', testId: 'nav-settings' },
  { to: ['closures'], label: 'closures.title', testId: 'nav-closures' },
  { to: [], label: 'venues.detail', short: 'nav.tab.more', testId: 'nav-venue' },
];

/**
 * The doors a thumb can reach, in the order a shift uses them. Five, because a sixth is a row
 * nobody can hit — which is why a short label is something a door is given, not something every
 * door has.
 */
export const VENUE_TABS: readonly VenueLink[] = [...VENUE_WORK, ...VENUE_BACK_OF_HOUSE].filter(
  (link) => link.short,
);

/** The count that belongs beside a door, or null when there is nothing waiting behind it. */
export function waitingOn(link: VenueLink, attention: VenueAttention | null): number | null {
  if (!attention || !link.waiting) {
    return null;
  }

  const count =
    link.waiting === 'slips' ? attention.slipsToCheck : attention.bookingsWithMoneyWaiting;

  return count > 0 ? count : null;
}
