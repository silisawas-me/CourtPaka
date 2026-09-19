import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  map,
  of,
  startWith,
  switchMap,
  tap,
} from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { PublicVenue, PublicVenueService } from '../../core/venues/public-venue.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { VenueAddressPipe } from '../../shared/venue-address.pipe';

/** Long enough that a search follows a pause in typing rather than every keystroke. */
const TYPING_PAUSE_MS = 250;

/**
 * Where a booker starts: the venues on the platform, searchable by name or by where they are
 * (PRD US-02). No session needed — signing in is for booking.
 */
@Component({
  selector: 'app-venue-search-page',
  imports: [
    VenueAddressPipe,
    ReactiveFormsModule,
    RouterLink,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './venue-search.page.html',
})
export class VenueSearchPage {
  private readonly venues = inject(PublicVenueService);

  protected readonly i18n = inject(TranslationService);

  protected readonly term = inject(FormBuilder).nonNullable.control('');
  protected readonly searching = signal(false);
  protected readonly error = signal<string | null>(null);

  /**
   * The results follow what is typed: switchMap drops the answer to a term the booker has already
   * moved on from, and a failed search leaves an empty list with the reason beside it rather than
   * ending the stream. The first listing is not a keystroke, so it is not made to wait for a pause;
   * typing back to the same term does not ask again.
   */
  protected readonly found = toSignal(
    this.term.valueChanges.pipe(
      debounceTime(TYPING_PAUSE_MS),
      map((term) => term.trim()),
      startWith(''),
      distinctUntilChanged(),
      tap(() => {
        this.searching.set(true);
        this.error.set(null);
      }),
      switchMap((term) =>
        this.venues.search(term).pipe(
          catchError((failure: unknown) => {
            this.error.set(errorKey(failure));
            return of([] as PublicVenue[]);
          }),
        ),
      ),
      tap(() => this.searching.set(false)),
    ),
    { initialValue: [] as PublicVenue[] },
  );
}
