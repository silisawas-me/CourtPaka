import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { controlOf, elementOf, pageProviders, textOf } from '../../testing/dom';
import { BookingPage } from './booking.page';

function booking(overrides: Record<string, unknown> = {}) {
  return {
    id: 'b1',
    venueId: 'v1',
    venueName: 'Smash Court',
    status: 'Held',
    createdAt: '2026-09-20T11:00:00Z',
    holdExpiresAt: '2026-09-20T11:15:00Z',
    totalBaht: 600,
    depositBaht: 600,
    slots: [
      { courtId: 'c1', courtName: 'Court 1', date: '2026-09-21', hour: 18, bahtPerHour: 300 },
      { courtId: 'c1', courtName: 'Court 1', date: '2026-09-21', hour: 19, bahtPerHour: 300 },
    ],
    slipUploadedAt: null,
    ...overrides,
  };
}

describe('BookingPage', () => {
  let fixture: ComponentFixture<BookingPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    vi.useFakeTimers();
    // The countdown is read against the wall clock, so the test owns it.
    vi.setSystemTime(new Date('2026-09-20T11:05:00Z'));

    await TestBed.configureTestingModule({
      imports: [BookingPage],
      providers: pageProviders([{ path: 'book', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy(); // Stops the countdown before the test ends.
    vi.useRealTimers();
    httpMock.verify();
  });

  function render(answer: object = booking(), payload: string | null = null): void {
    fixture = TestBed.createComponent(BookingPage);
    fixture.componentRef.setInput('bookingId', 'b1');
    fixture.detectChanges();

    httpMock.expectOne('/api/bookings/b1').flush(answer);
    fixture.detectChanges();
    answerHowToPay(payload);
  }

  /**
   * A booking with money owing also asks where to send it (PRD US-04). Answered with no payload,
   * so nothing here waits on the QR encoder — the tests that are about the code say so.
   */
  function answerHowToPay(payload: string | null = null): void {
    const asked = httpMock.match('/api/bookings/b1/payment');
    for (const request of asked) {
      request.flush({
        totalBaht: 600,
        depositBaht: 600,
        payAtVenueBaht: 0,
        holdExpiresAt: '2026-09-20T11:15:00Z',
        accountName: 'บริษัท ทดสอบ จำกัด',
        promptPayPayload: payload,
      });
    }
    fixture.detectChanges();
  }

  /**
   * The code carries the amount, so the booker does not type it — which is the whole reason the
   * payload is built on the server and only drawn here (PRD US-04).
   */
  it('draws the code the server built, with the account it is going to', async () => {
    render(booking(), '00020101021229370016A000000677010111');

    // The encoder is loaded and run asynchronously, and waiting for it means waiting on real
    // time rather than the clock the countdown tests own.
    vi.useRealTimers();
    await vi.waitUntil(() => {
      fixture.detectChanges();
      return elementOf(fixture, 'qr') !== null;
    });

    const drawn = elementOf<HTMLImageElement>(fixture, 'qr');
    expect(drawn?.getAttribute('src')).toMatch(/^data:image\/svg\+xml;charset=utf-8,/);
    expect(textOf(fixture, 'qr-account')).toBe('บริษัท ทดสอบ จำกัด');
  });

  it('says the venue is not set up rather than drawing a code that would be refused', () => {
    render();

    expect(elementOf(fixture, 'qr')).toBeNull();
    expect(textOf(fixture, 'qr-pending')).toBe(TRANSLATIONS.th['booking.pay.qrPending']);
  });

  it('does not ask how to pay for a booking that is over', () => {
    render(booking({ status: 'Cancelled' }));

    httpMock.expectNone('/api/bookings/b1/payment');
  });

  /** A file of the given size, which is all the page looks at before sending. */
  function file(bytes = 1024, name = 'slip.jpg'): File {
    return new File([new Uint8Array(bytes)], name, { type: 'image/jpeg' });
  }

  function choose(chosen: File): void {
    const input = controlOf(fixture, '[data-testid="send-slip"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [chosen], configurable: true });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('shows the hours, the total and what the booking is waiting for', () => {
    render();

    expect(textOf(fixture, 'venue-name')).toBe('Smash Court');
    expect(fixture.nativeElement.querySelectorAll('[data-testid=booking-slot]')).toHaveLength(2);
    expect(textOf(fixture, 'booking-total')).toContain('600');
    expect(textOf(fixture, 'booking-status')).toBe(TRANSLATIONS.th['booking.status.Held']);
  });

  it('counts down to the end of the hold', () => {
    render();

    expect(textOf(fixture, 'countdown')).toContain('10:00');

    vi.advanceTimersByTime(61_000);
    fixture.detectChanges();

    expect(textOf(fixture, 'countdown')).toContain('8:59');
  });

  it('says the hold ran out rather than counting past zero', () => {
    render();

    vi.setSystemTime(new Date('2026-09-20T11:20:00Z'));
    vi.advanceTimersByTime(1000);
    fixture.detectChanges();

    expect(textOf(fixture, 'countdown')).toBe(TRANSLATIONS.th['booking.pay.timeUp']);
  });

  it('sends the slip and shows what the booking became', () => {
    render();

    choose(file());

    const request = httpMock.expectOne('/api/bookings/b1/slip');
    expect(request.request.body).toBeInstanceOf(FormData);
    expect((request.request.body as FormData).get('file')).toBeInstanceOf(File);

    request.flush(
      booking({ status: 'PendingVerification', slipUploadedAt: '2026-09-20T11:06:00Z' }),
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-status')).toBe(
      TRANSLATIONS.th['booking.status.PendingVerification'],
    );
    expect(textOf(fixture, 'slip-sent')).toBe(TRANSLATIONS.th['booking.pay.slipSent']);
    // A better picture can still be sent while the venue is checking.
    expect(textOf(fixture, 'send-slip')).toBe(TRANSLATIONS.th['booking.pay.replaceSlip']);
  });

  it('refuses a file over five megabytes without asking the server', () => {
    render();

    choose(file(5 * 1024 * 1024 + 1));

    expect(textOf(fixture, 'upload-error')).toBe(TRANSLATIONS.th['error.slip.too_large']);
  });

  it('explains a refusal and reads the booking again, because it has usually moved on', () => {
    render();

    choose(file());
    httpMock
      .expectOne('/api/bookings/b1/slip')
      .flush({ code: 'slip.hold_expired' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'upload-error')).toBe(TRANSLATIONS.th['error.slip.hold_expired']);
    // The booking stays on screen while it is re-read, so the reason stays readable with it.
    expect(elementOf(fixture, 'booking-total')).not.toBeNull();

    httpMock.expectOne('/api/bookings/b1').flush(booking({ status: 'Expired' }));
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-status')).toBe(TRANSLATIONS.th['booking.status.Expired']);
    // Nothing left to pay for, so nothing left to send.
    expect(elementOf(fixture, 'send-slip')).toBeNull();
  });

  it('keeps the refusal on screen when the read that follows it also fails', () => {
    render();

    choose(file());
    httpMock
      .expectOne('/api/bookings/b1/slip')
      .flush({ code: 'slip.hold_expired' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    // The session went away between the two calls; the useful message must survive it.
    httpMock
      .expectOne('/api/bookings/b1')
      .flush({ code: 'auth.unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(textOf(fixture, 'upload-error')).toBe(TRANSLATIONS.th['error.slip.hold_expired']);
    expect(elementOf(fixture, 'booking-total')).not.toBeNull();
  });

  it('translates a booking that is not the reader s', () => {
    fixture = TestBed.createComponent(BookingPage);
    fixture.componentRef.setInput('bookingId', 'b1');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/bookings/b1')
      .flush({ code: 'booking.not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(TRANSLATIONS.th['error.booking.not_found']);
  });

  /**
   * A venue may hold the hours for part of the price and take the rest at the desk (PRD US-28).
   * Both numbers are said, because the one in the code is not the price and somebody who reads
   * only the price arrives at the counter thinking they have paid.
   */
  it('says what to transfer now and what is left for the venue', () => {
    render(booking({ totalBaht: 600, depositBaht: 200 }));

    expect(textOf(fixture, 'booking-total')).toContain('600');
    expect(textOf(fixture, 'booking-deposit')).toContain('200');
    expect(textOf(fixture, 'booking-at-venue')).toContain('400');
  });

  it('says nothing about a deposit where the whole price is asked for', () => {
    render(booking());

    expect(elementOf(fixture, 'booking-deposit')).toBeNull();
    expect(elementOf(fixture, 'booking-at-venue')).toBeNull();
  });
});
