import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { venueNow } from '../../core/i18n/plain-date';
import { WalkInEvents } from '../../core/venues/walk-in.events';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { WalkIn } from './walk-in';

describe('WalkIn', () => {
  let fixture: ComponentFixture<WalkIn>;
  let httpMock: HttpTestingController;
  const { date, hour } = venueNow();
  // A day open around the clock, so whatever hour the test runs at there is an hour to sell.
  const hours = Array.from({ length: 24 - hour }, (_, index) => hour + index);

  beforeEach(async () => {
    TestBed.configureTestingModule({ imports: [WalkIn], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(WalkIn);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    await fixture.whenStable();

    const asked = httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');
    // Asked as a refresh: the counter reading its own floor is not a page view.
    expect(asked.request.params.get('refresh')).toBe('true');
    asked.flush({
      venue: { id: 'v1', name: 'Smash' },
      date,
      lastBookableDate: date,
      opensHour: 0,
      closesHour: 24,
      courts: [
        {
          courtId: 'c1',
          name: 'Court 1',
          hours: hours.map((h) => ({ hour: h, status: 'Booked', bahtPerHour: 200 })),
        },
        {
          courtId: 'c2',
          name: 'Court 2',
          hours: hours.map((h) => ({ hour: h, status: 'Free', bahtPerHour: 200 })),
        },
      ],
    });
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('starts at the hour the clock is in, and offers only the courts free for it', () => {
    const now = elementOf(fixture, `walk-in-start-${hour}`);
    expect(now?.classList).toContain('on');
    expect(now?.textContent).toContain(TRANSLATIONS.th['walkIn.fromNow']);
    // Only the hour the clock is in is "now".
    if (hour < 23) {
      expect(textOf(fixture, `walk-in-start-${hour + 1}`)).not.toContain(
        TRANSLATIONS.th['walkIn.fromNow'],
      );
    }
    expect((elementOf(fixture, 'walk-in-court-c1') as HTMLButtonElement).disabled).toBe(true);
    expect((elementOf(fixture, 'walk-in-court-c2') as HTMLButtonElement).disabled).toBe(false);
  });

  it('says what is still missing on the button, until nothing is', () => {
    expect(textOf(fixture, 'walk-in-confirm')).toContain(TRANSLATIONS.th['walkIn.pickCourtFirst']);
    clickOn(fixture, 'walk-in-court-c2');
    expect(textOf(fixture, 'walk-in-confirm')).toContain(TRANSLATIONS.th['walkIn.nameFirst']);
  });

  it('sells it as the counter sells, and tells the page it is on', () => {
    const sold = vi.fn();
    TestBed.inject(WalkInEvents).sold.subscribe(sold);

    clickOn(fixture, 'walk-in-court-c2');
    const name = elementOf<HTMLInputElement>(fixture, 'walk-in-name')!;
    name.value = 'คุณกิ๊ฟ';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    clickOn(fixture, 'walk-in-paid-Cash');
    clickOn(fixture, 'walk-in-confirm');

    const request = httpMock.expectOne('/api/venues/v1/bookings');
    expect(request.request.body).toEqual({
      slots: [{ courtId: 'c2', date, hour }],
      customerName: 'คุณกิ๊ฟ',
      customerPhone: null,
      paidBy: 'Cash',
    });
    request.flush({ bookingId: 'b1' });

    expect(sold).toHaveBeenCalledWith({ venueId: 'v1' });
  });
});

describe('WalkIn opened on a tapped cell', () => {
  it('starts on that hour and court, even further off than the first few', () => {
    TestBed.configureTestingModule({ imports: [WalkIn], providers: pageProviders() });
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WalkIn);
    const { date, hour } = venueNow();
    const hours = Array.from({ length: 24 - hour }, (_, index) => hour + index);
    const last = hours[hours.length - 1];
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('at', { courtId: 'c2', hour: last });
    fixture.detectChanges();
    TestBed.tick();

    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush({
        venue: { id: 'v1', name: 'Smash' },
        date,
        lastBookableDate: date,
        opensHour: 0,
        closesHour: 24,
        courts: [
          {
            courtId: 'c2',
            name: 'Court 2',
            hours: hours.map((h) => ({ hour: h, status: 'Free', bahtPerHour: 200 })),
          },
        ],
      });
    fixture.detectChanges();

    expect(elementOf(fixture, `walk-in-start-${last}`)?.classList).toContain('on');
    expect(elementOf(fixture, 'walk-in-court-c2')?.classList).toContain('on');
    httpMock.verify();
  });
});
