import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
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
      {
        date: '2026-09-01',
        onlineBaht: 1200,
        staffBaht: 0,
        sellableHours: 32,
        bookedHours: 6,
        shopBaht: 0,
      },
      {
        date: '2026-09-02',
        onlineBaht: 0,
        staffBaht: 400,
        sellableHours: 32,
        bookedHours: 2,
        shopBaht: 0,
      },
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
    byMethod: [],
    topItems: [],
    priorBaht: 0,
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

  it('is the panel over the last fourteen days, and nothing else', () => {
    fixture = TestBed.createComponent(VenueDashboardPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    const asked = httpMock.expectOne((request) => request.url === '/api/venues/v1/dashboard');
    const from = new Date(asked.request.params.get('from')!);
    const to = new Date(asked.request.params.get('to')!);
    expect((to.getTime() - from.getTime()) / 86_400_000).toBe(13);
    asked.flush(figures());
    fixture.detectChanges();

    expect(elementOf(fixture, 'revenue-panel')).not.toBeNull();
    expect(elementOf(fixture, 'range-start')).toBeNull();
    expect(elementOf(fixture, 'download-csv')).toBeNull();
    expect(elementOf(fixture, 'revenue-total')).toBeNull();
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
