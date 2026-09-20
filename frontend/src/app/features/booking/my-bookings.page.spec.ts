import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { MyBookingsPage } from './my-bookings.page';

function booking(overrides: Record<string, unknown> = {}) {
  return {
    id: 'b1',
    venueId: 'v1',
    venueName: 'Development Court',
    status: 'Confirmed',
    createdAt: '2026-09-20T04:00:00Z',
    holdExpiresAt: '2026-09-20T04:15:00Z',
    totalBaht: 400,
    slots: [
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-22', hour: 18, bahtPerHour: 200 },
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-22', hour: 19, bahtPerHour: 200 },
    ],
    slipUploadedAt: null,
    paymentState: 'Received',
    refundDueBaht: 0,
    refundedBaht: 0,
    cancellation: {
      allowed: true,
      refused: null,
      refundPercent: 100,
      refundBaht: 400,
      awaitsVenue: false,
    },
    ...overrides,
  };
}

describe('MyBookingsPage', () => {
  let fixture: ComponentFixture<MyBookingsPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [MyBookingsPage],
      providers: pageProviders([
        { path: 'book', children: [] },
        { path: 'bookings/:bookingId', children: [] },
      ]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(upcoming: object[] = [], past: object[] = []): void {
    fixture = TestBed.createComponent(MyBookingsPage);
    fixture.detectChanges();

    httpMock.expectOne('/api/bookings').flush({ upcoming, past });
    fixture.detectChanges();
  }

  it('says so when the booker has taken nothing yet', () => {
    render();

    expect(textOf(fixture, 'nothing-yet')).toBe(TRANSLATIONS.th['myBookings.nothingYet']);
    expect(elementOf(fixture, 'find-a-court')).not.toBeNull();
  });

  it('puts what is ahead of them above what is behind', () => {
    render([booking()], [booking({ id: 'b2', status: 'Completed' })]);

    const groups = (fixture.nativeElement as HTMLElement).querySelectorAll('section');
    expect(groups[0].getAttribute('data-testid')).toBe('group-upcoming');
    expect(groups[1].getAttribute('data-testid')).toBe('group-past');
    expect(elementOf(fixture, 'nothing-yet')).toBeNull();
  });

  it('reads the hours a booking holds as one span', () => {
    render([booking()]);

    expect(textOf(fixture, 'booking-b1')).toContain('18:00 – 20:00');
    expect(textOf(fixture, 'booking-b1')).toContain('คอร์ท 1');
  });

  it('says what would come back before anything is given up', () => {
    render([booking()]);

    clickOn(fixture, 'let-go-b1');

    expect(textOf(fixture, 'refund-note')).toContain('400');
    expect(textOf(fixture, 'refund-note')).toContain(TRANSLATIONS.th['myBookings.refundWillBe']);
  });

  it('says the venue has still to confirm, when that is what decides the money', () => {
    render([
      booking({
        status: 'PendingVerification',
        paymentState: 'NotReceived',
        cancellation: {
          allowed: true,
          refused: null,
          refundPercent: 100,
          refundBaht: 0,
          awaitsVenue: true,
        },
      }),
    ]);

    clickOn(fixture, 'let-go-b1');

    expect(textOf(fixture, 'refund-note')).toBe(TRANSLATIONS.th['myBookings.refundAwaitsVenue']);
  });

  it('does not call an unpaid hold a loss', () => {
    render([
      booking({
        status: 'Held',
        paymentState: 'NotReceived',
        cancellation: {
          allowed: true,
          refused: null,
          refundPercent: 0,
          refundBaht: 0,
          awaitsVenue: false,
        },
      }),
    ]);

    clickOn(fixture, 'let-go-b1');

    expect(textOf(fixture, 'refund-note')).toBe(TRANSLATIONS.th['myBookings.refundNothingPaid']);
  });

  it('says plainly when money was paid and none of it comes back', () => {
    render([
      booking({
        cancellation: {
          allowed: true,
          refused: null,
          refundPercent: 0,
          refundBaht: 0,
          awaitsVenue: false,
        },
      }),
    ]);

    clickOn(fixture, 'let-go-b1');

    expect(textOf(fixture, 'refund-note')).toBe(TRANSLATIONS.th['myBookings.refundNothing']);
  });

  it('gives the hours up and reads the list again', () => {
    render([booking()]);

    clickOn(fixture, 'let-go-b1');
    clickOn(fixture, 'let-go-confirm');

    httpMock.expectOne('/api/bookings/b1/cancel').flush(booking({ status: 'Cancelled' }));
    fixture.detectChanges();

    // Where a cancelled booking belongs is the server's to say, so the page asks again.
    httpMock
      .expectOne('/api/bookings')
      .flush({ upcoming: [], past: [booking({ status: 'Cancelled' })] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'group-upcoming')).toBeNull();
    expect(elementOf(fixture, 'group-past')).not.toBeNull();
  });

  it('keeps the booking when the question is answered the other way', () => {
    render([booking()]);

    clickOn(fixture, 'let-go-b1');
    clickOn(fixture, 'keep');

    httpMock.expectNone('/api/bookings/b1/cancel');
    expect(elementOf(fixture, 'letting-go')).toBeNull();
  });

  it('explains a refusal and reads the list again, because the venue got there first', () => {
    render([booking()]);

    clickOn(fixture, 'let-go-b1');
    clickOn(fixture, 'let-go-confirm');

    httpMock
      .expectOne('/api/bookings/b1/cancel')
      .flush({ code: 'booking.play_has_started' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'cancel-error')).toBe(TRANSLATIONS.th['error.booking.play_has_started']);

    httpMock.expectOne('/api/bookings').flush({ upcoming: [booking()], past: [] });
    fixture.detectChanges();
  });

  it('offers no way out of a booking that has none', () => {
    render(
      [],
      [
        booking({
          status: 'Cancelled',
          refundDueBaht: 400,
          cancellation: {
            allowed: false,
            refused: 'booking.not_cancellable',
            refundPercent: 0,
            refundBaht: 0,
            awaitsVenue: false,
          },
        }),
      ],
    );

    expect(elementOf(fixture, 'let-go-b1')).toBeNull();
    // What is owed back is on the card, and so is what has actually been sent (PRD US-05).
    expect(textOf(fixture, 'refund-due')).toContain('400');
  });
});
