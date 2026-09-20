import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { VenueBookingsPage } from './venue-bookings.page';

function booking(overrides: Record<string, unknown> = {}) {
  return {
    bookingId: 'b1',
    bookerEmail: 'player@example.com',
    status: 'Confirmed',
    paymentState: 'Received',
    totalBaht: 400,
    refundDueBaht: 0,
    refundedBaht: 0,
    startsAt: '2026-09-21T11:00:00Z',
    endsAt: '2026-09-21T13:00:00Z',
    slots: [
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 18, bahtPerHour: 200 },
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 19, bahtPerHour: 200 },
    ],
    can: {
      cancel: true,
      noShow: false,
      settlePayment: false,
      playedAfterAll: false,
      refundPercentOnRequest: 100,
    },
    ...overrides,
  };
}

describe('VenueBookingsPage', () => {
  let fixture: ComponentFixture<VenueBookingsPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [VenueBookingsPage],
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(day: object[] = [booking()]): void {
    fixture = TestBed.createComponent(VenueBookingsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush(day);
    fixture.detectChanges();
  }

  it('says so when the day is empty', () => {
    render([]);

    expect(textOf(fixture, 'nothing-today')).toBe(TRANSLATIONS.th['venueBookings.nothingToday']);
  });

  it('reads a booking as its hours, its courts and who took it', () => {
    render();

    const row = textOf(fixture, 'booking-b1');
    expect(row).toContain('18:00 – 20:00');
    expect(row).toContain('คอร์ท 1');
    expect(row).toContain('player@example.com');
    expect(row).toContain('400');
  });

  it('offers only the doors the server says are open', () => {
    render([
      booking({
        can: {
          cancel: false,
          noShow: true,
          settlePayment: false,
          playedAfterAll: false,
          refundPercentOnRequest: 0,
        },
      }),
    ]);

    expect(elementOf(fixture, 'cancel-b1')).toBeNull();
    expect(elementOf(fixture, 'no-show-b1')).not.toBeNull();
  });

  it('asks why before it turns a paid booking away, and says what each answer gives back', () => {
    render([booking({ can: { ...booking().can, refundPercentOnRequest: 50 } })]);

    clickOn(fixture, 'cancel-b1');

    // The share the customer's own terms would give is shown beside the choice that uses it.
    expect(textOf(fixture, 'refund-on-request')).toContain('50%');
    expect(elementOf(fixture, 'reason-VenueInitiated')).not.toBeNull();
  });

  it('will not send a cancellation with no reason chosen', () => {
    render();

    clickOn(fixture, 'cancel-b1');
    clickOn(fixture, 'cancel-confirm');

    httpMock.expectNone('/api/venues/v1/bookings/b1/cancel');
    expect(textOf(fixture, 'decide-error')).toBe(TRANSLATIONS.th['error.booking.reason_required']);
  });

  it('sends the reason and the note, and replaces the row with what the server says', () => {
    render();

    clickOn(fixture, 'cancel-b1');
    clickOn(fixture, 'reason-VenueInitiated');
    setInput(fixture, '#note', 'ไฟดับทั้งสนาม');
    clickOn(fixture, 'cancel-confirm');

    const request = httpMock.expectOne('/api/venues/v1/bookings/b1/cancel');
    expect(request.request.body).toEqual({
      reason: 'VenueInitiated',
      paymentReceived: null,
      note: 'ไฟดับทั้งสนาม',
    });

    request.flush(
      booking({
        status: 'Cancelled',
        refundDueBaht: 400,
        can: { ...booking().can, cancel: false },
      }),
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-b1')).toContain(TRANSLATIONS.th['booking.status.Cancelled']);
    expect(textOf(fixture, 'refund-due-b1')).toContain('400');
    expect(elementOf(fixture, 'cancel-b1')).toBeNull();
  });

  it('asks about the money rather than a reason while the slip is still being checked', () => {
    render([
      booking({
        status: 'PendingVerification',
        paymentState: 'NotReceived',
      }),
    ]);

    clickOn(fixture, 'cancel-b1');

    // Nothing to reason about: the bank account either shows the transfer or it does not.
    expect(elementOf(fixture, 'reason-CustomerRequest')).toBeNull();
    clickOn(fixture, 'cancel-received');

    const request = httpMock.expectOne('/api/venues/v1/bookings/b1/cancel');
    expect(request.request.body).toEqual({
      reason: null,
      paymentReceived: true,
      note: null,
    });
    request.flush(booking({ status: 'Cancelled' }));
    fixture.detectChanges();
  });

  it('settles a booking the booker gave up mid-check', () => {
    render([
      booking({
        status: 'Cancelled',
        paymentState: 'Unconfirmed',
        can: { ...booking().can, cancel: false, settlePayment: true },
      }),
    ]);

    expect(elementOf(fixture, 'unsettled-b1')).not.toBeNull();
    clickOn(fixture, 'settle-b1');
    clickOn(fixture, 'settle-received');

    const request = httpMock.expectOne('/api/venues/v1/bookings/b1/settle-payment');
    expect(request.request.body).toEqual({ paymentReceived: true });
    request.flush(
      booking({
        status: 'Cancelled',
        paymentState: 'Received',
        refundDueBaht: 400,
        can: { ...booking().can, cancel: false, settlePayment: false },
      }),
    );
    fixture.detectChanges();

    expect(elementOf(fixture, 'unsettled-b1')).toBeNull();
    expect(textOf(fixture, 'refund-due-b1')).toContain('400');
  });

  it('will not take a no-show back without saying why', () => {
    render([
      booking({
        status: 'NoShow',
        can: { ...booking().can, cancel: false, playedAfterAll: true },
      }),
    ]);

    clickOn(fixture, 'played-b1');
    clickOn(fixture, 'played-confirm');

    httpMock.expectNone('/api/venues/v1/bookings/b1/played');
    expect(elementOf(fixture, 'played-reason-error')).not.toBeNull();
  });

  it('explains a refusal and reads the day again, because somebody decided first', () => {
    render([
      booking({
        status: 'Confirmed',
        can: { ...booking().can, noShow: true },
      }),
    ]);

    clickOn(fixture, 'no-show-b1');
    clickOn(fixture, 'no-show-confirm');

    httpMock
      .expectOne('/api/venues/v1/bookings/b1/no-show')
      .flush({ code: 'booking.not_yet_late_enough' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'decide-error')).toBe(
      TRANSLATIONS.th['error.booking.not_yet_late_enough'],
    );

    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush([booking()]);
    fixture.detectChanges();
  });

  it('translates a venue the reader may not look at', () => {
    fixture = TestBed.createComponent(VenueBookingsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/bookings')
      .flush({ code: 'venue.not_member' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(elementOf(fixture, 'page-error')).not.toBeNull();
  });
});
