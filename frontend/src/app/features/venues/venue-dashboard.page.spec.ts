import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
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
    months: [{ year: 2026, month: 9, onlineBaht: 1200, staffBaht: 400 }],
    attention: { slipsToCheck: 2, paymentsUnanswered: 1, refundsOutstanding: 0 },
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
