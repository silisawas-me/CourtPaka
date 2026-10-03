import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { pageProviders, textOf } from '../../testing/dom';
import { AdminDashboardPage } from './dashboard.page';

function figures(overrides: Record<string, unknown> = {}) {
  return {
    from: '2026-09-01',
    to: '2026-09-30',
    venuesByStatus: { Pending: 2, Approved: 5, Suspended: 1, Rejected: 0 },
    totals: { bookings: 30, onlineBookings: 20, staffBookings: 10, gmvBaht: 12500 },
    venues: [
      {
        venueId: 'v1',
        code: 'SBC',
        name: 'สนามทดสอบ',
        status: 'Approved',
        bookings: 30,
        onlineBookings: 20,
        staffBookings: 10,
        gmvBaht: 12500,
      },
    ],
    ...overrides,
  };
}

describe('AdminDashboardPage', () => {
  let fixture: ComponentFixture<AdminDashboardPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [AdminDashboardPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(from?: string, to?: string): void {
    fixture = TestBed.createComponent(AdminDashboardPage);
    if (from) {
      fixture.componentRef.setInput('from', from);
    }
    if (to) {
      fixture.componentRef.setInput('to', to);
    }
    fixture.detectChanges();
  }

  it('leads with GMV and shows each venue on a line', () => {
    render();
    httpMock.expectOne((request) => request.url === '/api/admin/dashboard').flush(figures());
    fixture.detectChanges();

    expect(textOf(fixture, 'gmv-total')).toContain('12,500');
    expect(textOf(fixture, 'channel-split')).toBe('20 : 10');
    expect(textOf(fixture, 'venues-Approved')).toBe('5');
    expect(textOf(fixture, 'venue-v1')).toContain('สนามทดสอบ');
  });

  it('asks for the range the URL holds', () => {
    render('2026-08-01', '2026-08-31');

    const request = httpMock.expectOne((one) => one.url === '/api/admin/dashboard');
    expect(request.request.params.get('from')).toBe('2026-08-01');
    request.flush(figures());
  });
});
