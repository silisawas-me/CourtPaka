import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
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
    arrival: 'Unconfirmed',
    arrivedAt: null,
    graceEndsAt: '2026-09-21T11:15:00Z',
    paymentState: 'Received',
    totalBaht: 400,
    takenBaht: 400,
    toPayBaht: 0,
    refundDueBaht: 0,
    sentBackBaht: 0,
    outstandingBaht: 0,
    slots: [
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 18, bahtPerHour: 200 },
      { courtId: 'c1', courtName: 'คอร์ท 1', date: '2026-09-21', hour: 19, bahtPerHour: 200 },
    ],
    can: {
      cancel: true,
      confirmArrival: false,
      checkIn: false,
      noShow: false,
      settlePayment: false,
      playedAfterAll: false,
      takeMoney: false,
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

  /** The floor the board is drawn on: the same answer the booker's grid comes from (US-25). */
  function grid(courts: object[] = [{ courtId: 'c1', name: 'คอร์ท 1', hours: [] }]): object {
    return {
      venue: { id: 'v1', name: 'Smash Court' },
      date: '2026-09-23',
      lastBookableDate: '2026-10-23',
      opensHour: 18,
      closesHour: 20,
      courts,
    };
  }

  function render(
    day: object[] = [booking()],
    floor: object | null = grid(),
    queue: object[] = [],
  ): void {
    fixture = TestBed.createComponent(VenueBookingsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    const asked = httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');
    if (floor) {
      asked.flush(floor);
    } else {
      asked.flush(null, { status: 503, statusText: 'Unavailable' });
    }

    // The queue for the day is asked for with it (PRD US-27).
    httpMock.expectOne((request) => request.url === '/api/venues/v1/waitlist').flush(queue);

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

    // Somebody who may not read this venue is refused both answers, not one.
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush({ code: 'venue.not_found' }, { status: 404, statusText: 'Not Found' });
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/waitlist')
      .flush({ code: 'venue.not_member' }, { status: 403, statusText: 'Forbidden' });
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
  describe('the day on one board (US-25) and who is coming (US-24)', () => {
    /** A day with two courts and three hours, one of them taken off sale. */
    function floor(): object {
      return {
        venue: { id: 'v1', name: 'Smash Court' },
        date: '2026-09-23',
        lastBookableDate: '2026-10-23',
        opensHour: 18,
        closesHour: 21,
        courts: [
          {
            courtId: 'c1',
            name: 'คอร์ท 1',
            hours: [
              { hour: 18, status: 'Booked', bahtPerHour: 200 },
              { hour: 19, status: 'Booked', bahtPerHour: 200 },
              { hour: 20, status: 'Free', bahtPerHour: 200 },
            ],
          },
          {
            courtId: 'c2',
            name: 'คอร์ท 2',
            hours: [
              { hour: 18, status: 'Free', bahtPerHour: 200 },
              { hour: 19, status: 'Closed', bahtPerHour: null },
              { hour: 20, status: 'Free', bahtPerHour: 200 },
            ],
          },
        ],
      };
    }

    it('draws a booking as one block over the hours it holds', () => {
      render([booking()], floor());

      // Two hours on one court is one block two wide, not two blocks.
      const block = elementOf(fixture, 'board-block-b1');
      expect(block).not.toBeNull();
      expect(block?.getAttribute('style')).toContain('--span: 2');

      // Named by the part before the @, not the whole address: a block is narrow, and the rest
      // of an address is the same for everybody at the same provider. The row below the board
      // still carries the whole of it.
      expect(block?.textContent).toContain('player');
      expect(block?.textContent).not.toContain('@example.com');

      // The hour nobody has taken is a button that sells it; the one off sale is not.
      expect(elementOf(fixture, 'board-free-c1-20')).not.toBeNull();
      expect(elementOf(fixture, 'board-closed-c2-19')).not.toBeNull();
      expect(elementOf(fixture, 'board-free-c2-19')).toBeNull();
    });

    it('says what an empty hour costs, which is what the phone is asking', () => {
      render([booking()], floor());

      // The price of the hour, from the same answer the board is drawn on — no second request.
      expect(textOf(fixture, 'board-free-c1-20')).toBe('200');
      // An hour with no price says nothing rather than nothing-shaped-like-a-number.
      expect(textOf(fixture, 'board-free-c2-20')).toBe('200');
      expect(elementOf(fixture, 'board-free-c1-20')?.getAttribute('aria-label')).toContain('200');
    });

    it('counts the day: what was booked, taken and still owed', () => {
      render([booking()], floor());

      // Two hours sold of the five that were on sale (one of six is closed).
      expect(textOf(fixture, 'today-bookings')).toBe('1');
      expect(textOf(fixture, 'today-used')).toBe('40%');
      expect(textOf(fixture, 'today-taken')).toBe('400');
      expect(textOf(fixture, 'today-owed')).toBe('0');
    });

    it('counts what is owed the way the server counts it, not from the price', () => {
      render(
        [
          // Half paid for, and still owing the rest.
          booking({
            paymentState: 'NotReceived',
            takenBaht: 200,
            toPayBaht: 200,
            can: { ...booking().can, takeMoney: true },
          }),
          // Turned away: it has a price, but the venue is not owed it (PRD US-26).
          booking({
            bookingId: 'b2',
            status: 'Cancelled',
            paymentState: 'NotReceived',
            takenBaht: 0,
            toPayBaht: 400,
            can: { ...booking().can, cancel: false, takeMoney: false },
          }),
        ],
        floor(),
      );

      expect(textOf(fixture, 'today-taken')).toBe('200');
      expect(textOf(fixture, 'today-owed')).toBe('200');
    });

    it('offers the counter the two things it writes down about a person', () => {
      render(
        [booking({ can: { ...booking().can, confirmArrival: true, checkIn: true } })],
        floor(),
      );

      expect(textOf(fixture, 'arrival-b1')).toBe(TRANSLATIONS.th['board.arrival.Unconfirmed']);

      clickOn(fixture, 'confirm-arrival-b1');
      httpMock
        .expectOne('/api/venues/v1/bookings/b1/confirm-arrival')
        .flush(booking({ arrival: 'Confirmed' }));
      fixture.detectChanges();

      expect(textOf(fixture, 'arrival-b1')).toBe(TRANSLATIONS.th['board.arrival.Confirmed']);
    });

    it('takes money at the desk and leaves the rest of the day where it was', () => {
      render(
        [
          booking({
            paymentState: 'NotReceived',
            takenBaht: 0,
            toPayBaht: 400,
            can: { ...booking().can, takeMoney: true },
          }),
        ],
        floor(),
      );

      expect(textOf(fixture, 'to-pay-b1')).toContain('400');

      clickOn(fixture, 'take-b1');
      // The amount is filled in with what is owed, because that is what usually changes hands.
      expect(elementOf<HTMLInputElement>(fixture, 'take-amount')?.value).toBe('400');

      setInput(fixture, '#take-note', 'รับเงินสดหน้าเคาน์เตอร์');
      clickOn(fixture, 'take-money');

      const request = httpMock.expectOne('/api/venues/v1/bookings/b1/payments');
      expect(request.request.body).toEqual({
        amountBaht: 400,
        method: 'Cash',
        note: 'รับเงินสดหน้าเคาน์เตอร์',
      });
      // The answer is the row as the server now draws it, like every other door: paying the last
      // of it can confirm the booking, and which doors that leaves open is the server's to say.
      request.flush(booking());
      fixture.detectChanges();

      expect(elementOf(fixture, 'to-pay-b1')).toBeNull();
      expect(textOf(fixture, 'today-taken')).toBe('400');
    });

    it('does not offer to take money where the server says there is none to take', () => {
      render([booking()], floor());

      expect(elementOf(fixture, 'take-b1')).toBeNull();
    });

    it('shows who wanted this day and did not get it', () => {
      render([booking()], floor(), [
        {
          id: 'w1',
          bookerEmail: 'waiting@example.com',
          bookerPhone: null,
          date: '2026-09-21',
          fromHour: 18,
          untilHour: 22,
          hours: 2,
          state: 'Waiting',
          askedAt: '2026-09-20T10:00:00Z',
        },
      ]);

      expect(textOf(fixture, 'waiting-w1')).toContain('waiting@example.com');
    });

    it('draws the day without a board when the floor cannot be read', () => {
      render([booking()], null);

      expect(elementOf(fixture, 'day-board')).toBeNull();
      expect(elementOf(fixture, 'booking-b1')).not.toBeNull();
    });
  });

  /**
   * The box is the page's own wiring around the rule: who each line names, and the five it shows
   * before it starts counting (PRD US-25). What goes on the list at all is `needs-doing.spec.ts`.
   */
  it('names whoever the row would name, and counts what it does not show', () => {
    const waiting = { ...booking().can, settlePayment: true };

    render([
      booking({
        bookingId: 'staff',
        channel: 'Staff',
        customerName: 'คุณเอ',
        bookerEmail: null,
        can: waiting,
      }),
      booking({ bookingId: 'gone', bookerEmail: null, bookerPhone: null, can: waiting }),
      ...['a', 'b', 'c', 'd'].map((id) => booking({ bookingId: id, can: waiting })),
    ]);

    expect(textOf(fixture, 'chore-settle-staff')).toContain('คุณเอ');
    // An address that is gone is said as gone rather than left blank (PRD S-15).
    expect(textOf(fixture, 'chore-settle-gone')).toContain(TRANSLATIONS.th['booker.deleted']);
    expect(textOf(fixture, 'more-chores')).toContain('1');
  });

  it('says nothing where the day is waiting for nothing', () => {
    render([booking()]);

    expect(elementOf(fixture, 'needs-doing')).toBeNull();
  });

  /**
   * One more hour, and a different court (PRD US-29). What is offered comes from the server when
   * the panel opens — which courts are free depends on the whole day, and a list carried on every
   * row would be out of date by the time anybody pressed it.
   */
  it('offers the courts the server says are free for the hour they would run on into', () => {
    render([booking({ can: { ...booking().can, extend: true, moveCourt: true } })]);

    clickOn(fixture, 'hours-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: {
        date: '2026-09-21',
        hour: 20,
        sameCourtId: 'c1',
        courts: [
          { courtId: 'c1', courtName: 'คอร์ท 1', baht: 300 },
          { courtId: 'c2', courtName: 'คอร์ท 2', baht: 300 },
        ],
      },
      move: { hours: 2, courts: [{ courtId: 'c2', courtName: 'คอร์ท 2', baht: null }] },
    });
    fixture.detectChanges();

    // The hour is named by when it ends, which is what the counter is agreeing to.
    expect(textOf(fixture, 'extend-until')).toContain('21:00');
    expect(textOf(fixture, 'extend-c1')).toContain(TRANSLATIONS.th['venueBookings.sameCourt']);
    expect(textOf(fixture, 'extend-c1')).toContain('300');
    expect(elementOf(fixture, 'move-c2')).not.toBeNull();

    // The court they are on is not offered as somewhere to move to.
    expect(elementOf(fixture, 'move-c1')).toBeNull();

    clickOn(fixture, 'extend-c2');
    const sent = httpMock.expectOne('/api/venues/v1/bookings/b1/extend');
    expect(sent.request.body).toEqual({ courtId: 'c2' });

    // The row is replaced with what the server now says, the way every other door works.
    sent.flush(
      booking({ totalBaht: 700, toPayBaht: 300, can: { ...booking().can, takeMoney: true } }),
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-b1')).toContain('700');
    expect(elementOf(fixture, 'take-b1')).not.toBeNull();
  });

  it('moves the hours to the court that was pressed', () => {
    render([booking({ can: { ...booking().can, extend: false, moveCourt: true } })]);

    clickOn(fixture, 'hours-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: null,
      move: { hours: 2, courts: [{ courtId: 'c2', courtName: 'คอร์ท 2', baht: null }] },
    });
    fixture.detectChanges();

    expect(elementOf(fixture, 'extend-until')).toBeNull();

    clickOn(fixture, 'move-c2');
    const sent = httpMock.expectOne('/api/venues/v1/bookings/b1/move');
    expect(sent.request.body).toEqual({ courtId: 'c2' });
    sent.flush(booking());
    fixture.detectChanges();

    expect(elementOf(fixture, 'asking')).toBeNull();
  });

  it('says so when no court is free rather than offering nothing at all', () => {
    render([booking({ can: { ...booking().can, extend: true, moveCourt: true } })]);

    clickOn(fixture, 'hours-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: { date: '2026-09-21', hour: 20, sameCourtId: 'c1', courts: [] },
      move: { hours: 2, courts: [] },
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'extend-none')).toBe(TRANSLATIONS.th['venueBookings.extendNone']);
    expect(textOf(fixture, 'move-none')).toBe(TRANSLATIONS.th['venueBookings.moveNone']);
  });

  it('does not offer the hours at all where the server says both doors are shut', () => {
    render([booking({ can: { ...booking().can, extend: false, moveCourt: false } })]);

    expect(elementOf(fixture, 'hours-b1')).toBeNull();
  });

  it('translates a refusal from the hours doors', () => {
    render([booking({ can: { ...booking().can, extend: true, moveCourt: false } })]);

    clickOn(fixture, 'hours-b1');
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: {
        date: '2026-09-21',
        hour: 20,
        sameCourtId: 'c1',
        courts: [{ courtId: 'c1', courtName: 'คอร์ท 1', baht: 300 }],
      },
      move: null,
    });
    fixture.detectChanges();

    clickOn(fixture, 'extend-c1');
    httpMock
      .expectOne('/api/venues/v1/bookings/b1/extend')
      .flush({ code: 'booking.hour_taken' }, { status: 409, statusText: 'Conflict' });
    // A refusal on a door is usually somebody else deciding first, so the day is read again.
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush([booking()]);
    fixture.detectChanges();

    expect(textOf(fixture, 'decide-error')).toBe(TRANSLATIONS.th['error.booking.hour_taken']);
  });
});
