import { arrivalTone, bookingTone, paymentTone } from './status-chip';

describe('status tones', () => {
  /**
   * The point of the four tones is that a counter can tell them apart across a room, so what
   * matters is which statuses share one and which do not.
   */
  it('reads a booking by what the venue has to do about it', () => {
    // Nothing to do: it is settled and ahead of us.
    expect(bookingTone('Confirmed')).toBe('confirmed');

    // Something is owed or unanswered.
    expect(bookingTone('Held')).toBe('waiting');
    expect(bookingTone('PendingVerification')).toBe('waiting');

    // Hours somebody had and gave back, or money that never came.
    expect(bookingTone('Cancelled')).toBe('risk');
    expect(bookingTone('Rejected')).toBe('risk');
    expect(bookingTone('NoShow')).toBe('risk');

    // Over, and nothing to answer for.
    expect(bookingTone('Completed')).toBe('done');
    expect(bookingTone('Expired')).toBe('done');
  });

  it('separates somebody here from somebody expected', () => {
    expect(arrivalTone('Arrived')).toBe('playing');
    expect(arrivalTone('Confirmed')).toBe('confirmed');
    expect(arrivalTone('Reminded')).toBe('waiting');
    expect(arrivalTone('Unconfirmed')).toBe('waiting');
  });

  /** Unanswered is not the same as unpaid, and the colours must not say it is (PRD 6.2). */
  it('separates money not answered for from money that did not come', () => {
    expect(paymentTone('Received')).toBe('playing');
    expect(paymentTone('Unconfirmed')).toBe('waiting');
    expect(paymentTone('NotReceived')).toBe('risk');
  });
});
