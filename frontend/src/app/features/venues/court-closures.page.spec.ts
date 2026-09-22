import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { CourtClosuresPage } from './court-closures.page';

function court(id: string, name: string) {
  return { id, name, position: 1, isActive: true };
}

function closure(overrides: Record<string, unknown> = {}) {
  return {
    id: 'x1',
    courtId: 'c1',
    startsOn: '2026-09-22',
    startHour: 18,
    endsOn: '2026-09-22',
    endHour: 20,
    reason: 'ซ่อมพื้น',
    createdAt: '2026-09-21T10:00:00Z',
    liftedAt: null,
    ...overrides,
  };
}

describe('CourtClosuresPage', () => {
  let fixture: ComponentFixture<CourtClosuresPage>;
  let httpMock: HttpTestingController;

  function render(courts: ReturnType<typeof court>[], closures: unknown[]): void {
    // The language is remembered in storage, and a spec that ran earlier in this worker may have
    // left English there; these assertions read the Thai words.
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [CourtClosuresPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CourtClosuresPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/courts').flush(courts);
    httpMock.expectOne('/api/venues/v1/closures').flush(closures);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('says when nothing is shut', () => {
    render([court('c1', 'คอร์ท 1')], []);

    expect(textOf(fixture, 'nothing-shut')).toBe(TRANSLATIONS.th['closures.nothingShut']);
  });

  it('sends the stretch as whole hours on the days that were picked', () => {
    render([court('c1', 'คอร์ท 1')], []);

    setInput(fixture, '[data-testid="start-hour"]', '18');
    setInput(fixture, '[data-testid="end-hour"]', '20');
    setInput(fixture, '[data-testid="reason"]', 'ซ่อมพื้น');
    clickOn(fixture, 'close-court');

    const request = httpMock.expectOne('/api/venues/v1/courts/c1/closures');
    expect(request.request.body.startHour).toBe(18);
    expect(request.request.body.endHour).toBe(20);
    expect(request.request.body.reason).toBe('ซ่อมพื้น');

    request.flush(closure());
    fixture.detectChanges();

    expect(elementOf(fixture, 'closure-x1')).not.toBeNull();
    expect(elementOf(fixture, 'nothing-shut')).toBeNull();
  });

  it('will not shut a court without saying why', () => {
    render([court('c1', 'คอร์ท 1')], []);

    setInput(fixture, '[data-testid="start-hour"]', '18');
    setInput(fixture, '[data-testid="end-hour"]', '20');
    clickOn(fixture, 'close-court');

    httpMock.expectNone('/api/venues/v1/courts/c1/closures');
    expect(elementOf(fixture, 'reason-error')).not.toBeNull();
  });

  /**
   * The refusal is the point of the screen: the venue is shown what stands in the way rather
   * than only told that something does (PRD US-11).
   */
  it('shows the bookings the server refused over', () => {
    render([court('c1', 'คอร์ท 1')], []);

    setInput(fixture, '[data-testid="reason"]', 'ซ่อมพื้น');
    clickOn(fixture, 'close-court');

    httpMock.expectOne('/api/venues/v1/courts/c1/closures').flush(
      {
        code: 'closure.bookings_in_the_way',
        bookings: [
          {
            bookingId: 'b1',
            courtId: 'c1',
            courtName: 'คอร์ท 1',
            date: '2026-09-22',
            fromHour: 18,
            toHour: 19,
            status: 'Confirmed',
          },
        ],
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(elementOf(fixture, 'in-the-way')).not.toBeNull();
    expect(textOf(fixture, 'in-the-way')).toContain(TRANSLATIONS.th['closures.inTheWay']);
    expect(textOf(fixture, 'clash-b1')).toContain('18:00');

    // The panel is the message. A line above it saying the same thing reads as a second problem.
    expect(elementOf(fixture, 'save-error')).toBeNull();
  });

  it('keeps a lifted closure on the list rather than dropping it', () => {
    render([court('c1', 'คอร์ท 1')], [closure()]);

    clickOn(fixture, 'lift-x1');
    httpMock
      .expectOne('/api/venues/v1/closures/x1/lift')
      .flush(closure({ liftedAt: '2026-09-21T11:00:00Z' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'closure-x1')).not.toBeNull();
    expect(elementOf(fixture, 'lifted-x1')).not.toBeNull();
    expect(elementOf(fixture, 'lift-x1')).toBeNull();
  });
});
