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
/**
 * Whether the person is coming, and then whether they came (PRD US-24). It is not the booking's
 * status: a booking that is paid for says nothing about somebody walking through the door.
 */
export type BookingArrival = 'Unconfirmed' | 'Reminded' | 'Confirmed' | 'Arrived';

export interface VenueBookingActions {
  cancel: boolean;
  /** Writing down that they said they are coming (PRD US-24). */
  confirmArrival: boolean;
  /** Taking them in at the desk. */
  checkIn: boolean;
  noShow: boolean;
  settlePayment: boolean;
  playedAfterAll: boolean;
  /** Taking money for it at the desk, in any form (PRD US-26). */
  takeMoney: boolean;
  /**
   * The reasons this booking may be turned away for, and what each owes the booker. Only the
   * ones the rules allow where it stands, so the page can never offer an answer the server
   * would refuse (PRD 6.1).
   */
  cancelChoices: CancelChoice[];
}

/** One answer the counter may give, and the money it settles (PRD US-13). */
export interface CancelChoice {
  reason: CancellationReason;
  refundBaht: number;
}

/** How somebody paid at the counter (PRD US-13). */
export type CounterPayment = 'Cash' | 'Transfer';

/** A booking taken at the counter for somebody standing at it (PRD US-13). */
export interface CounterBookingRequest {
  slots: { courtId: string; date: string; hour: number }[];
  customerName: string;
  customerPhone: string | null;
  paidBy: CounterPayment;
}

/** One of the venue's bookings for a day, as its counter reads it (PRD US-13). */
export interface VenueBooking {
  bookingId: string;
  bookerEmail: string | null;
  /** Only when there is no address: a LINE booker is reached on the phone (US-01). */
  bookerPhone: string | null;
  /** Online, or taken at the counter — which decides who the row is for. */
  channel: 'Online' | 'Staff';
  customerName: string | null;
  customerPhone: string | null;
  status: BookingStatus;
  /** Whether they are coming, and then whether they came (PRD US-24). */
  arrival: BookingArrival;
  arrivedAt: string | null;
  /** When being late turns into not having come, which is the venue's own wait (PRD US-24). */
  graceEndsAt: string;
  paymentState: 'NotReceived' | 'Received' | 'Unconfirmed';
  totalBaht: number;
  /** What the venue has taken for this booking so far, and what that leaves (PRD US-26). */
  takenBaht: number;
  toPayBaht: number;
  refundDueBaht: number;
  /** What the venue says it has sent back, and what that leaves to send (PRD 6.2, US-18). */
  sentBackBaht: number;
  outstandingBaht: number;
  slots: BookingSlot[];
  can: VenueBookingActions;
}

/** How money reached the venue (PRD US-26). */
export type PaymentMethod = 'Cash' | 'PromptPay' | 'Card';

/** One amount the venue took, as the counter reads it back. */
export interface PaymentReceipt {
  id: string;
  bookingId: string;
  amountBaht: number;
  method: PaymentMethod;
  receivedAt: string;
  note: string | null;
}

/** What one booking has been paid, and what that leaves (PRD US-26). */
export interface Takings {
  totalBaht: number;
  takenBaht: number;
  outstandingBaht: number;
  receipts: PaymentReceipt[];
}

/** A day's money, and the count at the end of it (PRD US-26). */
export interface DayMoney {
  date: string;
  takenBaht: number;
  cashBaht: number;
  promptPayBaht: number;
  cardBaht: number;
  cashRefundedBaht: number;
  outstandingBaht: number;
  cashReceipts: PaymentReceipt[];
  closed: DailyClosing | null;
}

export interface DailyClosing {
  date: string;
  openingFloatBaht: number;
  expectedCashBaht: number;
  countedCashBaht: number;
  differenceBaht: number;
  note: string | null;
  closedAt: string;
}

/** How the venue got the money back to the booker (PRD US-18). */
export type RefundMethod = 'Transfer' | 'Cash';

/** One transfer, as it was written down. Nothing about it changes afterwards (PRD US-18). */
export interface RefundRecord {
  id: string;
  amountBaht: number;
  refundedOn: string;
  method: RefundMethod;
  note: string | null;
  recordedAt: string;
  /** When it was taken back, if it was. A voided record still shows. */
  voidedAt: string | null;
  voidReason: string | null;
}

/** What a booking owes, what has been sent back, and what is left (PRD 6.2). */
export interface Refunds {
  refundDueBaht: number;
  sentBackBaht: number;
  outstandingBaht: number;
  records: RefundRecord[];
}

/** The counter's side of the bookings a venue has taken (PRD US-13). */
@Injectable({ providedIn: 'root' })
export class VenueBookingsService {
  private readonly http = inject(HttpClient);

  /** Selling hours to somebody standing at the counter; it starts confirmed (PRD US-13). */
  takeAtCounter(venueId: string, booking: CounterBookingRequest): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`/api/venues/${venueId}/bookings`, booking);
  }

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

  /** Writes down an amount the venue has just taken, and answers with the row it changed. */
  takePayment(
    venueId: string,
    bookingId: string,
    amountBaht: number,
    method: PaymentMethod,
    note?: string,
  ): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/payments`, {
      amountBaht,
      method,
      note,
    });
  }

  /** What a day took, by the form it came in, and whether it has been counted (PRD US-26). */
  money(venueId: string, date: string): Observable<DayMoney> {
    return this.http.get<DayMoney>(`/api/venues/${venueId}/money`, { params: { date } });
  }

  /** Counts the till and writes it down. The server works out what should be there. */
  closeDay(
    venueId: string,
    date: string,
    openingFloatBaht: number,
    countedCashBaht: number,
    note?: string,
  ): Observable<DailyClosing> {
    return this.http.post<DailyClosing>(
      `/api/venues/${venueId}/money/closing`,
      { openingFloatBaht, countedCashBaht, note },
      { params: { date } },
    );
  }

  /** They rang to say they are on their way, and the counter wrote it down (PRD US-24). */
  confirmArrival(venueId: string, bookingId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/confirm-arrival`, null);
  }

  /** They are at the desk. */
  checkIn(venueId: string, bookingId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/check-in`, null);
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

  /** What has been sent back for this booking, and what is still owed (PRD US-18). */
  refunds(venueId: string, bookingId: string): Observable<Refunds> {
    return this.http.get<Refunds>(`${this.at(venueId, bookingId)}/refunds`);
  }

  /** Writing down a transfer the venue has already made. */
  recordRefund(
    venueId: string,
    bookingId: string,
    refund: { amountBaht: number; refundedOn: string; method: RefundMethod; note?: string },
  ): Observable<Refunds> {
    return this.http.post<Refunds>(`${this.at(venueId, bookingId)}/refunds`, {
      ...refund,
      note: refund.note ?? null,
    });
  }

  /** Taking one back, which puts the amount onto what the venue still owes. The owner's alone. */
  voidRefund(
    venueId: string,
    bookingId: string,
    refundId: string,
    reason: string,
  ): Observable<Refunds> {
    return this.http.post<Refunds>(`${this.at(venueId, bookingId)}/refunds/${refundId}/void`, {
      reason,
    });
  }

  private at(venueId: string, bookingId: string): string {
    return `/api/venues/${venueId}/bookings/${bookingId}`;
  }
}
