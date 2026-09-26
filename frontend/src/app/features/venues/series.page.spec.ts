import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { SeriesPage } from './series.page';

function court(id: string, name: string) {
  return { id, name, position: 1, isActive: true };
}

function series(overrides: Record<string, unknown> = {}) {
  return {
    seriesId: 's1',
    courtId: 'c1',
    courtName: 'คอร์ท 1',
    day: 'Tuesday',
    fromHour: 18,
    untilHour: 20,
    customerName: 'ก๊วนอังคาร',
    customerPhone: '0812345678',
    startsOn: '2026-10-06',
    untilOn: null,
    state: 'Running',
    endedAt: null,
    endReason: null,
    booked: 3,
    missed: [],
    ...overrides,
  };
}

describe('SeriesPage', () => {
  let fixture: ComponentFixture<SeriesPage>;
  let httpMock: HttpTestingController;

  function render(courts: ReturnType<typeof court>[], standing: unknown[]): void {
    // The language is remembered in storage, and a spec that ran earlier in this worker may have
    // left English there; these assertions read the Thai words.
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [SeriesPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SeriesPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/courts').flush(courts);
    httpMock.expectOne('/api/venues/v1/series').flush(standing);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('says when no group comes every week', () => {
    render([court('c1', 'คอร์ท 1')], []);

    expect(textOf(fixture, 'nothing-standing')).toBe(TRANSLATIONS.th['series.nothingStanding']);
  });

  it('sends the arrangement as a weekday and a window of whole hours', () => {
    render([court('c1', 'คอร์ท 1')], []);

    clickOn(fixture, 'day-Tuesday');
    setInput(fixture, '[data-testid="from-hour"]', '18');
    setInput(fixture, '[data-testid="until-hour"]', '20');
    setInput(fixture, '[data-testid="customer-name"]', 'ก๊วนอังคาร');
    setInput(fixture, '[data-testid="customer-phone"]', '0812345678');
    clickOn(fixture, 'save-series');

    const request = httpMock.expectOne('/api/venues/v1/series');
    expect(request.request.body.courtId).toBe('c1');
    expect(request.request.body.day).toBe('Tuesday');
    expect(request.request.body.fromHour).toBe(18);
    expect(request.request.body.untilHour).toBe(20);
    expect(request.request.body.customerName).toBe('ก๊วนอังคาร');

    // No last week by default: a group comes until somebody stops them (PRD US-30).
    expect(request.request.body.untilOn).toBeNull();

    request.flush(series());
    fixture.detectChanges();

    expect(elementOf(fixture, 'series-s1')).not.toBeNull();
  });

  it('will not send an arrangement with nobody on it', () => {
    render([court('c1', 'คอร์ท 1')], []);

    clickOn(fixture, 'save-series');

    httpMock.expectNone('/api/venues/v1/series');
    expect(textOf(fixture, 'customer-name-error')).not.toBe('');
  });

  /**
   * The weeks that could not be made are the reason to open this page at all, so they are on the
   * row rather than behind something that has to be opened (PRD US-30).
   */
  it('shows the weeks that could not be booked, and why, in words', () => {
    render(
      [court('c1', 'คอร์ท 1')],
      [
        series({
          missed: [
            { date: '2026-10-13', refusal: 'booking.slot_just_taken' },
            { date: '2026-10-20', refusal: 'booking.hour_not_available' },
          ],
        }),
      ],
    );

    const missed = elementOf(fixture, 'missed-s1');
    expect(missed).not.toBeNull();
    // Said as what it means to a week a month away, not as the words somebody who just pressed
    // a button would be given.
    expect(missed!.textContent).toContain(TRANSLATIONS.th['series.missed.booking.slot_just_taken']);
    expect(missed!.textContent).toContain(
      TRANSLATIONS.th['series.missed.booking.hour_not_available'],
    );

    expect(textOf(fixture, 'weeks-to-sort-out')).toContain('2');
  });

  /**
   * `endedAt` is a moment, not a day, and reading a moment with the plain-date pipe renders
   * nothing at all — silently. The row would then say "stopped on" and stop there.
   */
  it('says when a group that has stopped stopped', () => {
    render(
      [court('c1', 'คอร์ท 1')],
      [series({ state: 'Ended', endedAt: '2026-09-26T04:00:00Z', booked: 4 })],
    );

    const over = elementOf(fixture, 'ended-list');
    expect(over!.textContent).toContain(TRANSLATIONS.th['series.endedOn']);
    expect(over!.textContent).toContain('2569');
  });

  it('stopping one says how many weeks it cancelled', () => {
    render([court('c1', 'คอร์ท 1')], [series()]);

    // Asked twice: the answer cancels weeks of bookings and may owe money back.
    clickOn(fixture, 'stop-s1');
    fixture.detectChanges();
    httpMock.expectNone('/api/venues/v1/series/s1/stop');

    setInput(fixture, '[data-testid="stop-reason-s1"]', 'ก๊วนเลิกเล่น');
    clickOn(fixture, 'stop-confirm-s1');

    const request = httpMock.expectOne('/api/venues/v1/series/s1/stop');
    expect(request.request.body.note).toBe('ก๊วนเลิกเล่น');

    request.flush({
      series: series({ state: 'Ended', endedAt: '2026-09-26T04:00:00Z' }),
      cancelled: 3,
      left: 0,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'last-stop')).toContain('3');

    // It leaves the running list and joins the ones that are over.
    expect(elementOf(fixture, 'nothing-standing')).not.toBeNull();
    expect(elementOf(fixture, 'ended-list')).not.toBeNull();
  });

  /**
   * Changing one starts from what it already says — the point is usually that one thing about it
   * moved, not that the group is new (PRD US-30).
   */
  it('changing one fills the form from the arrangement and posts the change', () => {
    render([court('c1', 'คอร์ท 1'), court('c2', 'คอร์ท 2')], [series()]);

    clickOn(fixture, 'change-s1');
    fixture.detectChanges();

    const name = elementOf(fixture, 'customer-name') as HTMLInputElement;
    expect(name.value).toBe('ก๊วนอังคาร');

    clickOn(fixture, 'court-c2');
    clickOn(fixture, 'save-series');

    const request = httpMock.expectOne('/api/venues/v1/series/s1/change');
    expect(request.request.body.courtId).toBe('c2');
    expect(request.request.body.customerName).toBe('ก๊วนอังคาร');

    request.flush({
      series: series({ seriesId: 's2', courtId: 'c2', courtName: 'คอร์ท 2' }),
      cancelled: 1,
      left: 0,
    });

    // The one that was replaced has ended, which only the list can say.
    httpMock
      .expectOne('/api/venues/v1/series')
      .flush([
        series({ state: 'Ended', endedAt: '2026-09-26T04:00:00Z' }),
        series({ seriesId: 's2', courtId: 'c2', courtName: 'คอร์ท 2' }),
      ]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'series-s2')).not.toBeNull();
    expect(elementOf(fixture, 'ended-list')).not.toBeNull();
  });
});
