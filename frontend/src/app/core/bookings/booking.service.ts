/** One court-hour, named the way the grid names it. */
export interface BookingSlotRequest {
  courtId: string;
  date: string;
  hour: number;
}

export interface BookingSlot extends BookingSlotRequest {
  courtName: string;
  bahtPerHour: number;
}

/** Whether the venue considers itself paid (PRD 6.2). Unanswered is not the same as unpaid. */
export type PaymentState = 'NotReceived' | 'Received' | 'Unconfirmed';

export type BookingStatus =
  | 'Held'
  | 'PendingVerification'
  | 'Confirmed'
  | 'Completed'
  | 'Cancelled'
  | 'Expired'
  | 'Rejected'
  | 'NoShow';

/**
 * What letting a booking go right now would come to, worked out by the server so the number shown
 * before the button and the number given after it are the same one (PRD US-05).
 */
export interface CancellationOffer {
  allowed: boolean;
  /** The share of the booking this would give back, which a tiered policy makes worth saying. */
  refundPercent: number;
  refundBaht: number;
  /** The venue has yet to say whether the money arrived, so the amount is not settled. */
  awaitsVenue: boolean;
}

export interface Booking {
  id: string;
  venueId: string;
  venueName: string;
  status: BookingStatus;
  createdAt: string;
  /** When the hold lapses if it has not been paid for (PRD BR-02). */
  holdExpiresAt: string;
  totalBaht: number;
  slots: BookingSlot[];
  /** When the booker last sent a slip, if they have (PRD US-04). */
  slipUploadedAt: string | null;
  /** Whether the venue has the money, and what it owes back (PRD 6.2). */
  paymentState: PaymentState;
  refundDueBaht: number;
  refundedBaht: number;
  /**
   * What had to arrive to hold these hours (PRD US-28). The same as the price unless the venue
   * asked for a share of it up front, and then the difference is paid at the venue.
   */
  depositBaht: number;
  /** Why that much was asked for (PRD US-28). A name the page turns into a sentence. */
  depositReason: DepositReason;
  /**
   * What is still owed at the venue, decided by the server (PRD US-28). Nought unless this is a
   * booking the venue will honour and its price has not all arrived — a booking that was let go
   * has a difference too, and nobody owes it.
   */
  toPayBaht: number;
  cancellation: CancellationOffer;
}

/**
 * Why a booking was asked for what it was asked for (PRD US-28): what the venue asks everybody,
 * or what it asks somebody who has left hours unused here.
 */
export type DepositReason = 'VenueTerms' | 'SomeNoShows' | 'ManyNoShowsAtPeak';

/** A booker's own bookings, split by the server, which holds the clock (PRD US-05). */
