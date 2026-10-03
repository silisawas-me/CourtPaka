import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BookingSlot, BookingStatus, PaymentState } from '../bookings/booking.service';

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
  /** Settling it with hours somebody bought earlier instead (PRD US-31). */
  payWithPackage: boolean;
  /** Selling them the hour they would run on into (PRD US-29). */
  extend: boolean;
  /** Putting the hours they have not played on another court (PRD US-29). */
  moveCourt: boolean;
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
  /** The share of the price it gives back, 0–100 (PRD 6.1). */
  refundPercent: number;
  /** For the customer's request: the notice the next tier asks for, which this falls short of. */
  underHours: number | null;
}

/** How somebody paid at the counter (PRD US-13). */
export type CounterPayment = 'Cash' | 'Transfer';

/** A booking taken at the counter for somebody standing at it (PRD US-13). */
export interface CounterBookingRequest {
  slots: { courtId: string; date: string; hour: number }[];
  customerName: string;
  customerPhone: string | null;
  paidBy: CounterPayment | null;
  /** Hours the customer bought earlier, instead of money (PRD US-31). */
  packageId?: string | null;
}

/** One of the venue's bookings for a day, as its counter reads it (PRD US-13). */
/** Booked in the app, sold at the counter, a group's standing week, or hours from a package. */
export type BookingKind = 'App' | 'WalkIn' | 'Series' | 'Package';

/** In the order the floor's legend reads them. */
export const BOOKING_KINDS: readonly BookingKind[] = ['App', 'WalkIn', 'Series', 'Package'];

/** One line of a booking's story, as the server read it from the record (the booking list). */
export interface BookingHistoryEntry {
  at: string;
  kind: 'Status' | 'Arrival' | 'Hours' | 'Payment' | 'Refund' | 'RefundVoided';
  from: string | null;
  to: string | null;
  amountBaht: number | null;
  method: string | null;
  fromCourt: string | null;
  toCourt: string | null;
  hours: number;
  cause: string | null;
  by: string | null;
}

export interface VenueBooking {
  bookingId: string;
  bookerEmail: string | null;
  /** Only when there is no address: a LINE booker is reached on the phone (US-01). */
  bookerPhone: string | null;
  /** What the booker asked to be called, when they said ("คุณแพร"). */
  bookerName?: string | null;
  /** Online, or taken at the counter — which decides who the row is for. */
  channel: 'Online' | 'Staff';
  /** Which of the four the floor colours it as — worked out by the server (`BookingKinds`). */
  kind: BookingKind;
  customerName: string | null;
  customerPhone: string | null;
  status: BookingStatus;
  /** Whether they are coming, and then whether they came (PRD US-24). */
  arrival: BookingArrival;
  arrivedAt: string | null;
  /** When being late turns into not having come, which is the venue's own wait (PRD US-24). */
  graceEndsAt: string;
  paymentState: PaymentState;
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

/**
 * A court these hours could be played on, and what they would cost there (PRD US-29). The amount
 * is null where the door does not change what anything costs, which moving does not.
 */
export interface FreeCourt {
  courtId: string;
  courtName: string;
  baht: number | null;
}

/**
 * What could still be done with a booking's hours (PRD US-29). Either half is null where that
 * door is shut, which the server decides — the page only draws what it is given.
 */
export interface BookingHours {
  extend: {
    date: string;
    hour: number;
    /** The court they are on, which is the one taken unless the venue picks another. */
    sameCourtId: string | null;
    courts: FreeCourt[];
  } | null;
  move: { hours: number; courts: FreeCourt[] } | null;
  /** The last hour, which could come off before it begins, and what it was sold for. */
  shorten: { date: string; hour: number; courtId: string; baht: number } | null;
}

/**
 * How money reached the venue (PRD US-26). One list, in the order a counter reaches for them, so
 * that a screen offering a choice of method cannot quietly be missing one.
 */
export const PAYMENT_METHODS = ['Cash', 'PromptPay', 'Card', 'BankTransfer', 'TrueMoney'] as const;

export type PaymentMethod = (typeof PAYMENT_METHODS)[number];

/** One amount the venue took, as the counter reads it back. */
export interface PaymentReceipt {
  id: string;
  /** What the money was for: one booking, one package sold, or one shop sale. */
  bookingId: string | null;
  packageId?: string | null;
  saleId?: string | null;
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

/** Which kind of row might be what the till is out by (PRD US-26). */
export type MoneyLeadKind = 'CashTaken' | 'CashHandedBack' | 'CashPaidOut' | 'StillOwed';

/**
 * One row whose amount is exactly what the count came out by. It is where to look, not what
 * happened — the person who was at the desk decides that.
 */
export interface MoneyLead {
  kind: MoneyLeadKind;
  amountBaht: number;
  bookingId: string | null;
  at: string | null;
  note: string | null;
}

/** A day's money, and the count at the end of it (PRD US-26). */
export interface DayMoney {
  date: string;
  takenBaht: number;
  cashBaht: number;
  promptPayBaht: number;
  cardBaht: number;
  /** Cash handed back: to a booker, or over the counter for a sale taken back (US-18, US-32). */
  cashRefundedBaht: number;
  /** Cash the venue paid out. Out of the same drawer, but not back to anybody (US-33). */
  cashPaidOutBaht: number;
  outstandingBaht: number;
  cashReceipts: PaymentReceipt[];
  closed: DailyClosing | null;
  /** Empty until the latest count, and empty when it came out even. */
  leads: MoneyLead[];
  /** Straight into the bank account, and TrueMoney (thai-fit T3). Not in the till. */
  bankTransferBaht: number;
  trueMoneyBaht: number;
  /** Every count of the day so far, shifts and then the close (thai-fit T2). */
  counts: DailyClosing[];
  /** The drawer since the last count, while the day is still open. */
  openShift: OpenShift | null;
  /** Every movement of the day's money, in and out, oldest first (the drawer page's list). */
  lines?: MoneyLine[];
  /** When the venue opened that day: where its first shift is said to start. */
  opensHour?: number | null;
}

/** One movement of money, as data the drawer page words (thai-fit T2). */
export interface MoneyLine {
  at: string;
  /** True for money leaving: a refund, a bill paid from the drawer, a sale handed back. */
  out: boolean;
  /** In: Court · Sale · Package. Out: Refunded · PaidOut · SaleTakenBack. */
  kind: string;
  method: PaymentMethod;
  amountBaht: number;
  who: string | null;
  courts: string | null;
  bookingKind: string | null;
  /** Deposit or Rest, where a booking was paid in more than one go. */
  part: string | null;
  items: { name: string; quantity: number }[] | null;
  packageHours: number | null;
  spendKind: string | null;
  note: string | null;
  by: string | null;
  bookingId?: string | null;
}

/** The shift running now: from the last count, the cash it has taken and paid out. */
export interface OpenShift {
  from: string;
  cashInBaht: number;
  cashOutBaht: number;
}

export interface DailyClosing {
  date: string;
  openingFloatBaht: number;
  expectedCashBaht: number;
  countedCashBaht: number;
  differenceBaht: number;
  note: string | null;
  closedAt: string;
  /** True for the count that closed the day; false for a shift handing over. */
  endsDay: boolean;
  closedBy: string | null;
  /** Where this count's shift started. */
  from: string | null;
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
  /**
   * The most the person reading may write down in one record (PRD US-18), or null where they
   * have no ceiling. The server decides it; the page only repeats what it said.
   */
  yourLimitBaht: number | null;
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

  /** Bookings whose customer's name or phone holds what was typed, on any day. */
  find(venueId: string, q: string): Observable<VenueBooking[]> {
    return this.http.get<VenueBooking[]>(`/api/venues/${venueId}/bookings/find`, {
      params: { q },
    });
  }

  /** What has happened to one booking, oldest first. */
  history(venueId: string, bookingId: string): Observable<BookingHistoryEntry[]> {
    return this.http.get<BookingHistoryEntry[]>(
      `/api/venues/${venueId}/bookings/${bookingId}/history`,
    );
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
  /** A day's money; with no date, the server's own today for the venue (thai-fit T4). */
  money(venueId: string, date: string | null): Observable<DayMoney> {
    return this.http.get<DayMoney>(`/api/venues/${venueId}/money`, {
      params: date ? { date } : {},
    });
  }

  /** Counts the till and writes it down. The server works out what should be there. */
  closeDay(
    venueId: string,
    date: string,
    openingFloatBaht: number,
    countedCashBaht: number,
    note?: string,
    endsDay = true,
  ): Observable<DailyClosing> {
    return this.http.post<DailyClosing>(
      `/api/venues/${venueId}/money/closing`,
      { openingFloatBaht, countedCashBaht, note, endsDay },
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

  /** What could still be done with this booking's hours, asked of the server (PRD US-29). */
  hours(venueId: string, bookingId: string): Observable<BookingHours> {
    return this.http.get<BookingHours>(`${this.at(venueId, bookingId)}/hours`);
  }

  /** One more hour, on the court they are on unless the venue names another (PRD US-29). */
  /** One hour fewer: the last one, before it begins (the owner app's "−1 ชม."). */
  shorten(venueId: string, bookingId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/shorten`, null);
  }

  extend(venueId: string, bookingId: string, courtId?: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/extend`, {
      courtId: courtId ?? null,
    });
  }

  /** The hours they have not played, on another court (PRD US-29). */
  moveCourt(venueId: string, bookingId: string, courtId: string): Observable<VenueBooking> {
    return this.http.post<VenueBooking>(`${this.at(venueId, bookingId)}/move`, { courtId });
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
