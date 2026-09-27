import { Component, computed, inject, input } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { catchError, filter, map, merge, of, switchMap } from 'rxjs';
import { TranslationService } from '../core/i18n/translation.service';
import { VenueService } from '../core/venues/venue.service';
import { VENUE_BACK_OF_HOUSE, VENUE_TABS, VENUE_WORK, VenueLink, waitingOn } from './venue-nav';

/** A door with its route already built and its count already read. */
interface Door {
  readonly path: unknown[];
  readonly label: string;
  readonly short: string;
  readonly testId: string;
  /** The same door, named for the bar it is in, so a script says which one it pressed. */
  readonly tabTestId: string;
  readonly exact: boolean;
  readonly waiting: number | null;
}

/**
 * The venue's own navigation: down the side of a desk, along the bottom of a phone (PRD US-25).
 *
 * Its own component rather than part of the app shell because every page of the app pays for what
 * the root component carries, and the front door, sign-in and "my venues" have no counter, no
 * shift and no venue.
 */
@Component({
  imports: [RouterLink, RouterLinkActive],
  selector: 'app-venue-shell',
  styleUrl: './venue-shell.scss',
  templateUrl: './venue-shell.html',
})
export class VenueShell {
  private readonly venues = inject(VenueService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  /**
   * What is waiting at this venue (PRD US-17), read when the shell arrives and again on every
   * move between its pages — which is when the number can have changed, because the move is
   * usually somebody having just dealt with one of them. A venue whose counts cannot be read
   * draws no numbers: the doors still work, and the server decides those anyway. `switchMap`
   * drops the answer for the venue that was left, which would otherwise land last and put its
   * counts under these doors.
   */
  private readonly attention = toSignal(
    merge(
      toObservable(this.venueId),
      this.router.events.pipe(
        filter((event) => event instanceof NavigationEnd),
        map(() => this.venueId()),
      ),
    ).pipe(
      switchMap((venueId) => this.venues.attention(venueId).pipe(catchError(() => of(null)))),
      takeUntilDestroyed(),
    ),
    { initialValue: null },
  );

  protected readonly groups = computed(() => [
    { heading: 'nav.atTheCounter', doors: this.doorsOf(VENUE_WORK) },
    { heading: 'nav.behindTheCounter', doors: this.doorsOf(VENUE_BACK_OF_HOUSE) },
  ]);

  protected readonly tabs = computed(() => this.doorsOf(VENUE_TABS));

  /**
   * Routes and counts resolved once per venue, not once per change detection: a fresh array
   * handed to `routerLink` on every pass makes the router rebuild and re-compare a URL tree for
   * every door, on every tick, on every page.
   */
  private doorsOf(links: readonly VenueLink[]): Door[] {
    const venueId = this.venueId();
    const attention = this.attention();

    return links.map((link) => ({
      path: ['/venues', venueId, ...link.to],
      label: link.label,
      short: link.short ?? link.label,
      testId: link.testId,
      tabTestId: link.testId.replace('nav-', 'tab-'),
      // The venue's own page is a prefix of every other door, so only it asks for an exact match.
      exact: link.to.length === 0,
      waiting: waitingOn(link, attention),
    }));
  }
}
