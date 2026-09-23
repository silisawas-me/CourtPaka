import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { WAITLIST_MAX_HOURS, WaitlistService } from '../../core/bookings/waitlist.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';

/**
 * Taking a place in the queue for a day that had nothing on it (PRD US-27).
 *
 * Its own component so the grid page can leave it out of the first load: the form fields it needs
 * are the only reason the booker's grid would carry Material's form machinery, and the grid is the
 * page PRD 8's LCP target is about. Whoever wants a queue presses for it, and pressing is what
 * fetches it — the same bargain the calendar makes.
 */
@Component({
  selector: 'app-waitlist-card',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  templateUrl: './waitlist-card.html',
})
export class WaitlistCard implements OnInit {
  private readonly waitlist = inject(WaitlistService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly date = input.required<string>();

  /** The hours this venue sells that day: the widest window anybody can wait for. */
  readonly hours = input.required<readonly number[]>();

  /** Whether there is a session; without one the card offers the way to get one. */
  readonly signedIn = input.required<boolean>();

  /** A place was taken, so the page can stop offering to take one. */
  readonly joined = output<string>();

  protected readonly waiting = signal(false);
  protected readonly waitingError = signal<string | null>(null);
  protected readonly took = signal(false);

  protected readonly waitForm = this.forms.nonNullable.group({
    fromHour: this.forms.nonNullable.control(0),
    untilHour: this.forms.nonNullable.control(0),
    hours: this.forms.nonNullable.control(1),
  });

  /** The hours a window may end on: one past each hour the venue sells. */
  protected readonly closeHours = computed(() => this.hours().map((hour) => hour + 1));

  /**
   * How long a run anybody may ask for here: never more than the day is open, so the form cannot
   * offer a want this venue could not answer even if it were empty.
   */
  protected readonly hourChoices = computed(() => {
    const most = Math.min(WAITLIST_MAX_HOURS, this.hours().length);
    return Array.from({ length: Math.max(most, 1) }, (_, index) => index + 1);
  });

  /**
   * The window comes from the day rather than from a guess, and it is filled in once: the card is
   * built when somebody asks for it, so there is no refresh to undo the choice they then make.
   */
  ngOnInit(): void {
    const hours = this.hours();
    if (hours.length > 0) {
      this.waitForm.patchValue({
        fromHour: hours[0],
        untilHour: hours[hours.length - 1] + 1,
      });
    }
  }

  protected wait(): void {
    if (this.waiting()) {
      return;
    }

    const { fromHour, untilHour, hours } = this.waitForm.getRawValue();
    this.waiting.set(true);
    this.waitingError.set(null);

    this.waitlist.join(this.venueId(), this.date(), fromHour, untilHour, hours).subscribe({
      next: (entry) => {
        this.waiting.set(false);
        this.took.set(true);
        this.joined.emit(entry.id);
      },
      error: (failure: unknown) => {
        this.waiting.set(false);
        this.waitingError.set(errorKey(failure));
      },
    });
  }
}
