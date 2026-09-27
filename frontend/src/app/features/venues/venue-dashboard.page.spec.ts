import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenueDashboardPage } from './venue-dashboard.page';

function figures(overrides: Record<string, unknown> = {}) {
  return {
    from: '2026-09-01',
    to: '2026-09-02',
    onlineBaht: 1200,
    staffBaht: 400,
    advanceBaht: 600,
    sellableHours: 64,
    bookedHours: 8,
    utilizationPercent: 12.5,
    days: [
      { date: '2026-09-01', onlineBaht: 1200, staffBaht: 0, sellableHours: 32, bookedHours: 6 },
      { date: '2026-09-02', onlineBaht: 0, staffBaht: 400, sellableHours: 32, bookedHours: 2 },
    ],
    months: [
      {
        year: 2026,
        month: 9,
        onlineBaht: 1200,
        staffBaht: 400,
        bookings: 5,
        refundDueBaht: 100,
        refundedBaht: 50,
      },
    ],
    attention: { slipsToCheck: 2, paymentsUnanswered: 1, refundsOutstanding: 0 },
    recovery: {
      hoursLost: 0,
      hoursRefilled: 0,
      refilledBaht: 0,
      hoursFromQueue: 0,
      fromQueueBaht: 0,
    },
    owedHours: { hours: 0, baht: 0, packages: 0, runningOut: 0 },
    trade: { shopBaht: 0, spentBaht: 0, leftOverBaht: 0 },
    ...overrides,
  };
}

describe('VenueDashboardPage', () => {
  let fixture: ComponentFixture<VenueDashboardPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [VenueDashboardPage],
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(answer: object = figures(), from?: string, to?: string): void {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    if (from) {
      fixture.componentRef.setInput('from', from);
    }
    if (to) {
      fixture.componentRef.setInput('to', to);
    }
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/dashboard').flush(answer);
    fixture.detectChanges();
  }

  it('leads with what the venue kept, online and at the counter together', () => {
    render();

    expect(textOf(fixture, 'revenue-total')).toContain('1,600');
    expect(textOf(fixture, 'revenue-online')).toBe('1,200');
    expect(textOf(fixture, 'revenue-counter')).toBe('400');
    expect(textOf(fixture, 'advance')).toBe('600');
  });

  /**
   * Hours somebody bought and has not used are an obligation, not earnings — the money arrived
   * when the package was sold (PRD US-31, S-27).
   */
  it('shows hours it still owes, apart from the money', () => {
    render(figures({ owedHours: { hours: 14, baht: 2520, packages: 2, runningOut: 1 } }));

    expect(textOf(fixture, 'owed-hours')).toContain('14');
    expect(textOf(fixture, 'owed-running-out')).toContain('1');
  });

  it('says nothing about owed hours when none are owed', () => {
    render(figures());

    expect(elementOf(fixture, 'owed-hours')).toBeNull();
  });

  it('shows utilisation with the hours behind it', () => {
    render();

    expect(textOf(fixture, 'utilization')).toContain('12.5%');
    expect(textOf(fixture, 'utilization')).toContain('8 / 64');
  });

  it('says there was nothing to sell rather than 0%', () => {
    render(figures({ utilizationPercent: null, sellableHours: 0, bookedHours: 0 }));

    expect(textOf(fixture, 'utilization')).toBe(TRANSLATIONS.th['dashboard.nothingToSell']);
  });

  it('asks for the range the URL holds', () => {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('from', '2026-08-01');
    fixture.componentRef.setInput('to', '2026-08-31');
    fixture.detectChanges();

    const request = httpMock.expectOne((one) => one.url === '/api/venues/v1/dashboard');
    expect(request.request.params.get('from')).toBe('2026-08-01');
    expect(request.request.params.get('to')).toBe('2026-08-31');
    request.flush(figures());
  });

  it('drops an older answer once a newer range is asked for', () => {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('from', '2026-08-01');
    fixture.componentRef.setInput('to', '2026-08-31');
    fixture.detectChanges();
    const older = httpMock.expectOne((one) => one.params.get('from') === '2026-08-01');

    fixture.componentRef.setInput('from', '2026-09-01');
    fixture.componentRef.setInput('to', '2026-09-30');
    fixture.detectChanges();
    const newer = httpMock.expectOne((one) => one.params.get('from') === '2026-09-01');

    expect(older.cancelled).toBe(true);
    newer.flush(figures({ from: '2026-09-01', to: '2026-09-30' }));
  });

  it('lists every day of the range', () => {
    render();

    expect(elementOf(fixture, 'day-2026-09-01')).not.toBeNull();
    expect(textOf(fixture, 'day-2026-09-02')).toContain('2 / 32');
  });

  it('only draws the month table when there is more than one month', () => {
    render();
    expect(elementOf(fixture, 'months')).toBeNull();

    fixture.destroy();
    render(
      figures({
        months: [
          { year: 2026, month: 8, onlineBaht: 1, staffBaht: 0 },
          { year: 2026, month: 9, onlineBaht: 2, staffBaht: 0 },
        ],
      }),
    );
    expect(elementOf(fixture, 'months')).not.toBeNull();
  });

  it('says what was lost and what came back, and how much of it the queue did', () => {
    render(
      figures({
        recovery: {
          hoursLost: 6,
          hoursRefilled: 4,
          refilledBaht: 900,
          hoursFromQueue: 3,
          fromQueueBaht: 700,
        },
      }),
    );

    expect(textOf(fixture, 'hours-lost')).toBe('6');
    expect(textOf(fixture, 'hours-refilled')).toContain('900');
    expect(textOf(fixture, 'from-queue')).toContain('3');
  });

  it('says nothing about recovery on a range that lost nothing', () => {
    render();

    expect(elementOf(fixture, 'hours-lost')).toBeNull();
  });

  it('hands the months over as a file named for the range', () => {
    render();

    // What is checked is the file the page offers, not how many times the browser was asked for
    // a url: other specs share this environment and call the same globals, so a count here
    // would fail on somebody else's work (it did, in CI).
    const offered: { name: string; href: string }[] = [];
    const created = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:made');
    const revoked = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const clicked = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      offered.push({ name: this.download, href: this.href });
    });

    clickOn(fixture, 'download-csv');

    expect(offered).toEqual([{ name: 'courtpaka-2026-09-01-2026-09-02.csv', href: 'blob:made' }]);
    // And the blob is let go once the click has been handled, or the page holds it for ever.
    expect(revoked).toHaveBeenCalledWith('blob:made');

    created.mockRestore();
    revoked.mockRestore();
    clicked.mockRestore();
  });

  it('counts what is waiting and links to where it is dealt with', () => {
    render();

    expect(textOf(fixture, 'slips-to-check')).toBe('2');
    expect(elementOf(fixture, 'slips-to-check')?.getAttribute('href')).toBe(
      '/venues/v1/slip-queue',
    );
    expect(textOf(fixture, 'payments-unanswered')).toBe('1');
  });

  it('moves to last month through the URL', async () => {
    render();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    clickOn(fixture, 'last-month');

    const [, extras] = navigate.mock.calls[0];
    expect(extras?.queryParams?.['from']).toMatch(/^\d{4}-\d{2}-01$/);
  });

  it('tells somebody without the permission what they are missing', () => {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/dashboard')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(TRANSLATIONS.th['dashboard.noPermission']);
  });

  it('says what went wrong when the figures cannot be read', () => {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/dashboard')
      .flush({ code: 'dashboard.invalid_range' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(TRANSLATIONS.th['error.dashboard.invalid_range']);
  });
});
