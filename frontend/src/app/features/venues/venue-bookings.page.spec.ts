import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { VenueBookingsPage } from './venue-bookings.page';

function record(id: string, amountBaht: number, overrides: Record<string, unknown> = {}) {
  return {
    id,
    amountBaht,
    refundedOn: '2026-09-20',
    method: 'Transfer',
    note: null,
    recordedAt: '2026-09-20T10:00:00Z',
    voidedAt: null,
    voidReason: null,
    ...overrides,
  };
}

function booking(overrides: Record<string, unknown> = {}) {
  return {
    bookingId: 'b1',
    bookerEmail: 'player@example.com',
    status: 'Confirmed',
    paymentState: 'Received',
    totalBaht: 400,
    refundDueBaht: 0,
    sentBackBaht: 0,
    outstandingBaht: 0,
    slots: [
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 18, bahtPerHour: 200 },
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 19, bahtPerHour: 200 },
    ],
    can: {
      cancel: true,
      noShow: false,
      settlePayment: false,
      playedAfterAll: false,
      cancelChoices: [
        { reason: 'CustomerRequest', refundBaht: 400 },
        { reason: 'VenueInitiated', refundBaht: 400 },
        { reason: 'PaymentNotReceived', refundBaht: 0 },
      ],
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

  it('names a booker who asked to be forgotten as a deleted account, not as nobody', () => {
    render([booking({ bookerEmail: null, channel: 'Online' })]);

    expect(textOf(fixture, 'who-b1')).toContain(TRANSLATIONS.th['booker.deleted']);
  });

  it('offers only the doors the server says are open', () => {
    render([
      booking({
        can: {
          cancel: false,
          noShow: true,
          settlePayment: false,
          playedAfterAll: false,
          cancelChoices: [],
        },
      }),
    ]);

    expect(elementOf(fixture, 'cancel-b1')).toBeNull();
    expect(elementOf(fixture, 'no-show-b1')).not.toBeNull();
  });

  it('says what each answer would give back, in baht, beside the answer', () => {
    render([
      booking({
        can: {
          ...booking().can,
          cancelChoices: [
            { reason: 'CustomerRequest', refundBaht: 200 },
            { reason: 'VenueInitiated', refundBaht: 400 },
          ],
        },
      }),
    ]);

    clickOn(fixture, 'cancel-b1');

    expect(textOf(fixture, 'gives-CustomerRequest')).toContain('200');
    expect(textOf(fixture, 'gives-VenueInitiated')).toContain('400');
  });

  it('offers only the answers the server allows for this booking', () => {
    render([
      booking({
        status: 'Completed',
        can: {
          ...booking().can,
          cancelChoices: [
            { reason: 'VenueInitiated', refundBaht: 400 },
            { reason: 'PaymentNotReceived', refundBaht: 0 },
          ],
        },
      }),
    ]);

    clickOn(fixture, 'cancel-b1');

    // Hours already played cannot be given back at the customer's asking (PRD 6.1), so that
    // answer is not there to pick.
    expect(elementOf(fixture, 'reason-CustomerRequest')).toBeNull();
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

  it('shows what has been sent back and what is left, and writes down a transfer', () => {
    render([booking({ status: 'Cancelled', refundDueBaht: 400, outstandingBaht: 400 })]);

    clickOn(fixture, 'refunds-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/refunds').flush({
      refundDueBaht: 400,
      sentBackBaht: 0,
      outstandingBaht: 400,
      records: [],
    });
    fixture.detectChanges();

    // The amount is filled in with what is still owed: sending all of it is what usually happens.
    expect(textOf(fixture, 'outstanding')).toContain('400');
    clickOn(fixture, 'record-refund');

    const request = httpMock.expectOne('/api/venues/v1/bookings/b1/refunds');
    expect(request.request.body.amountBaht).toBe(400);
    expect(request.request.body.method).toBe('Transfer');

    request.flush({
      refundDueBaht: 400,
      sentBackBaht: 400,
      outstandingBaht: 0,
      records: [
        {
          id: 'r1',
          amountBaht: 400,
          refundedOn: '2026-09-20',
          method: 'Transfer',
          note: null,
          recordedAt: '2026-09-20T10:00:00Z',
          voidedAt: null,
          voidReason: null,
        },
      ],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'sent-back')).toContain('400');
    expect(textOf(fixture, 'all-sent')).toBe(TRANSLATIONS.th['refunds.allSent']);
    // And the row itself now says there is nothing left to send.
    expect(textOf(fixture, 'outstanding-b1')).toBe(`${TRANSLATIONS.th['refunds.outstanding']}: 0`);
  });

  it('will not take a record back without saying why', () => {
    render([booking({ status: 'Cancelled', refundDueBaht: 400, outstandingBaht: 0 })]);

    clickOn(fixture, 'refunds-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/refunds').flush({
      refundDueBaht: 400,
      sentBackBaht: 400,
      outstandingBaht: 0,
      records: [
        {
          id: 'r1',
          amountBaht: 400,
          refundedOn: '2026-09-20',
          method: 'Cash',
          note: null,
          recordedAt: '2026-09-20T10:00:00Z',
          voidedAt: null,
          voidReason: null,
        },
      ],
    });
    fixture.detectChanges();

    clickOn(fixture, 'ask-void-r1');
    clickOn(fixture, 'void-r1');

    httpMock.expectNone('/api/venues/v1/bookings/b1/refunds/r1/void');
    expect(elementOf(fixture, 'void-reason-error')).not.toBeNull();
  });

  it('sends the reason typed against the record it was typed against', () => {
    render([booking({ status: 'Cancelled', refundDueBaht: 400, outstandingBaht: 0 })]);

    clickOn(fixture, 'refunds-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/refunds').flush({
      refundDueBaht: 400,
      sentBackBaht: 400,
      outstandingBaht: 0,
      records: [record('r1', 200), record('r2', 200)],
    });
    fixture.detectChanges();

    // A reason typed for one record and then abandoned must not travel to the next one: these
    // rows cannot be corrected afterwards.
    clickOn(fixture, 'ask-void-r1');
    setInput(fixture, '[data-testid="void-reason-r1"]', 'โอนไม่สำเร็จ');
    clickOn(fixture, 'ask-void-r2');
    expect(elementOf<HTMLInputElement>(fixture, 'void-reason-r2')!.value).toBe('');

    setInput(fixture, '[data-testid="void-reason-r2"]', 'กดผิด');
    clickOn(fixture, 'void-r2');

    const request = httpMock.expectOne('/api/venues/v1/bookings/b1/refunds/r2/void');
    expect(request.request.body.reason).toBe('กดผิด');
  });

  it('does not show one booking’s refunds under another', () => {
    render([
      booking({ bookingId: 'b1', status: 'Cancelled', refundDueBaht: 400, outstandingBaht: 400 }),
      booking({ bookingId: 'b2', status: 'Cancelled', refundDueBaht: 100, outstandingBaht: 100 }),
    ]);

    clickOn(fixture, 'refunds-b1');
    const first = httpMock.expectOne('/api/venues/v1/bookings/b1/refunds');

    // The counter moved on before the first answer arrived.
    clickOn(fixture, 'refunds-b2');
    const second = httpMock.expectOne('/api/venues/v1/bookings/b2/refunds');

    first.flush({ refundDueBaht: 400, sentBackBaht: 0, outstandingBaht: 400, records: [] });
    fixture.detectChanges();
    expect(elementOf(fixture, 'outstanding')).toBeNull();

    second.flush({ refundDueBaht: 100, sentBackBaht: 0, outstandingBaht: 100, records: [] });
    fixture.detectChanges();
    expect(textOf(fixture, 'outstanding')).toContain('100');
  });

  it('offers no way to write down money on a booking that owes none', () => {
    render([booking()]);

    expect(elementOf(fixture, 'refunds-b1')).toBeNull();
  });
});
