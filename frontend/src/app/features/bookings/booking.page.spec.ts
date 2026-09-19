import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
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

  function render(answer: object = booking()): void {
    fixture = TestBed.createComponent(BookingPage);
    fixture.componentRef.setInput('bookingId', 'b1');
    fixture.detectChanges();

    httpMock.expectOne('/api/bookings/b1').flush(answer);
    fixture.detectChanges();
  }

  /** A file of the given size, which is all the page looks at before sending. */
  function file(bytes = 1024, name = 'slip.jpg'): File {
    return new File([new Uint8Array(bytes)], name, { type: 'image/jpeg' });
  }

  function choose(chosen: File): void {
    const input = elementOf<HTMLInputElement>(fixture, 'send-slip')!.querySelector('input')!;
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

    httpMock.expectOne('/api/bookings/b1').flush(booking({ status: 'Expired' }));
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-status')).toBe(TRANSLATIONS.th['booking.status.Expired']);
    // Nothing left to pay for, so nothing left to send.
    expect(elementOf(fixture, 'send-slip')).toBeNull();
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
});
