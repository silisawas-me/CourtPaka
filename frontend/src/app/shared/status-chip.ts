import { Component, input } from '@angular/core';
import { BookingStatus, PaymentState } from '../core/bookings/booking.service';
import { BookingArrival } from '../core/venues/venue-bookings.service';

/**
 * What a status means, in the four readings a counter can tell apart at a glance: it is being
 * played or the money is in, it is settled and ahead of us, it is not sure yet, or it is lost.
 * `done` is the fifth quiet one — over, and nothing to do about it.
 */
export type StatusTone = 'playing' | 'confirmed' | 'waiting' | 'risk' | 'done';

/**
 * The tone of a booking's own status (PRD 6.1). Confirmed reads as "expected" rather than as
 * "good": the hours are still ahead, and what is ahead is not yet money.
 */
export function bookingTone(status: BookingStatus): StatusTone {
  switch (status) {
    case 'Confirmed':
      return 'confirmed';
    case 'Held':
    case 'PendingVerification':
      return 'waiting';
    case 'Rejected':
    case 'Cancelled':
    case 'NoShow':
      return 'risk';
    case 'Expired':
    case 'Completed':
      return 'done';
  }
}

/** Whether somebody has said they are coming, and whether they have arrived (PRD US-24). */
export function arrivalTone(arrival: BookingArrival): StatusTone {
  switch (arrival) {
    case 'Arrived':
      return 'playing';
    case 'Confirmed':
      return 'confirmed';
    case 'Unconfirmed':
    case 'Reminded':
      return 'waiting';
  }
}

/** Whether the venue has the money (PRD 6.2). Unanswered is not the same as not paid. */
export function paymentTone(state: PaymentState): StatusTone {
  switch (state) {
    case 'Received':
      return 'playing';
    case 'NotReceived':
      return 'risk';
    case 'Unconfirmed':
      return 'waiting';
  }
}

/**
 * One status, drawn the same way wherever it appears (PRD US-25, US-23).
 *
 * The words are passed in already translated, because they belong to the screen's own language
 * and its own i18n keys; what this owns is the colour, so that "waiting" is the same yellow on
 * the board, in the day's list, in the slip queue and in a booker's history. Before this, each
 * page picked its own, and three Material containers cut from one green meant nothing to anybody.
 */
@Component({
  selector: 'app-status-chip',
  imports: [],
  template: `<span class="badge" [class]="'tone-' + tone()" [attr.data-testid]="testId()">{{
    label()
  }}</span>`,
  styles: `
    :host {
      display: inline-flex;
    }
  `,
})
export class StatusChip {
  readonly label = input.required<string>();
  readonly tone = input.required<StatusTone>();
  readonly testId = input<string | null>(null);
}
