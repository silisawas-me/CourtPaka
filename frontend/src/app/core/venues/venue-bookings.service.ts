import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BookingSlot, BookingStatus } from '../bookings/booking.service';

/** Why the venue turned a booking away. Each answer settles a different amount (PRD 6.1). */
export type CancellationReason = 'CustomerRequest' | 'VenueInitiated' | 'PaymentNotReceived';

/**
 * Which of the counter's doors are open on a booking, and what the money would do. The server
 * works these out from the same rules the writes go through, so the page draws buttons from this
 * rather than from rules of its own (PRD US-13).
 */
export interface VenueBookingActions {
  cancel: boolean;
  noShow: boolean;
  settlePayment: boolean;
  playedAfterAll: boolean;
  /** What cancelling at the customer's request would give back, when that is an answer. */
  refundPercentOnRequest: number;
}

/** One of the venue's bookings for a day, as its counter reads it (PRD US-13). */
export interface VenueBooking {
  bookingId: string;
  bookerEmail: string | null;
  status: BookingStatus;
  paymentState: 'NotReceived' | 'Received' | 'Unconfirmed';
  totalBaht: number;
  refundDueBaht: number;
  refundedBaht: number;
  startsAt: string;
  endsAt: string;
  slots: BookingSlot[];
  can: VenueBookingActions;
}

/** The counter's side of the bookings a venue has taken (PRD US-13). */
@Injectable({ providedIn: 'root' })
export class VenueBookingsService {
  private readonly http = inject(HttpClient);

  day(venueId: string, date: string): Observable<VenueBooking[]> {
    return this.http.get<VenueBooking[]>(`/api/venues/${venueId}/bookings`, {
      params: { date },
    });
  }

  cancel(
    venueId: string,
    bookingId: string,
    answers: { reason?: CancellationReason; paymentReceived?: boolean; note?: string },
  ): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/cancel`, {
      reason: answers.reason ?? null,
      paymentReceived: answers.paymentReceived ?? null,
      note: answers.note ?? null,
    });
  }

  noShow(venueId: string, bookingId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/no-show`, null);
  }

  playedAfterAll(venueId: string, bookingId: string, reason: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/played`, { reason });
  }

  settlePayment(
    venueId: string,
    bookingId: string,
    paymentReceived: boolean,
  ): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/settle-payment`, {
      paymentReceived,
    });
  }

  private at(venueId: string, bookingId: string): string {
    return `/api/venues/${venueId}/bookings/${bookingId}`;
  }
}
