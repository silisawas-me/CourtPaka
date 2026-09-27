import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { venueNow } from '../../core/i18n/plain-date';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { NowPage } from './now.page';

describe('NowPage', () => {
  let fixture: ComponentFixture<NowPage>;
  let httpMock: HttpTestingController;

  // The page reads the wall clock, so the fixtures are built around whatever hour it is now.
  const { hour, date } = venueNow();

  function booking(overrides: Record<string, unknown> = {}) {
    return {
      bookingId: 'b1',
      bookerEmail: 'player@example.com',
      bookerPhone: null,
      channel: 'Online',
      kind: 'App',
      customerName: null,
      customerPhone: null,
      status: 'Confirmed',
      arrival: 'Unconfirmed',
      arrivedAt: null,
      graceEndsAt: '2026-09-28T11:15:00Z',
      paymentState: 'Received',
      totalBaht: 200,
      takenBaht: 200,
      toPayBaht: 0,
      refundDueBaht: 0,
      sentBackBaht: 0,
      outstandingBaht: 0,
      slots: [{ courtId: 'c1', courtName: 'Court 1', date, hour, bahtPerHour: 200 }],
      can: { checkIn: true, cancelChoices: [] },
      ...overrides,
    };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [NowPage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(day: object[]): void {
    fixture = TestBed.createComponent(NowPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    const grid = httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');
    // Asked as a refresh, so a counter watching its own floor is not counted as a page view.
    expect(grid.request.params.get('refresh')).toBe('true');
    grid.flush({
      venue: { id: 'v1', name: 'Smash' },
      date,
      lastBookableDate: date,
      opensHour: 0,
      closesHour: 24,
      courts: [
        { courtId: 'c1', name: 'Court 1', hours: [{ hour, status: 'Free', bahtPerHour: 200 }] },
      ],
    });
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush(day);
    fixture.detectChanges();
  }

  it('draws a court with somebody due on it, and takes them in from the card', () => {
    render([booking()]);

    expect(elementOf(fixture, 'now-court-c1')?.classList).toContain('due');

    clickOn(fixture, 'now-check-in-b1');
    httpMock
      .expectOne('/api/venues/v1/bookings/b1/check-in')
      .flush(booking({ arrival: 'Arrived', can: { checkIn: false, cancelChoices: [] } }));
    fixture.detectChanges();

    // The answer replaces the row: the court is being played on now.
    expect(elementOf(fixture, 'now-court-c1')?.classList).toContain('playing');
    expect(elementOf(fixture, 'now-check-in-b1')).toBeNull();
  });

  it('draws a free court with what the hour costs', () => {
    render([]);

    expect(elementOf(fixture, 'now-court-c1')?.classList).toContain('free');
    expect(textOf(fixture, 'now-court-c1')).toContain('200');
    expect(elementOf(fixture, 'now-nobody-arriving')).not.toBeNull();
  });

  it('offers a quick sale with no booking attached', () => {
    render([]);

    expect(elementOf(fixture, 'sell-onto-booking')).not.toBeNull();
  });
});
