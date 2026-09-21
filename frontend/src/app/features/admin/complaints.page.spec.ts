import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import {
  check,
  clickOn,
  elementOf,
  pageProviders,
  setInput,
  submitForm,
  textOf,
} from '../../testing/dom';
import { AdminComplaintsPage } from './complaints.page';

function summary(overrides: Record<string, unknown> = {}) {
  return {
    id: 'k1',
    bookingId: 'b1',
    venueName: 'สนามทดสอบ',
    channel: 'Line',
    status: 'Open',
    openedAt: '2026-09-21T10:00:00Z',
    resolvedAt: null,
    ...overrides,
  };
}

function complaint(overrides: Record<string, unknown> = {}) {
  return {
    id: 'k1',
    details: 'โอนแล้วแต่สนามไม่ยืนยัน',
    channel: 'Line',
    status: 'Open',
    openedAt: '2026-09-21T10:00:00Z',
    openedByEmail: 'admin@example.com',
    resolvedAt: null,
    resolvedByEmail: null,
    resolution: null,
    booking: {
      bookingId: 'b1',
      venueId: 'v1',
      venueCode: 'SBC',
      venueName: 'สนามทดสอบ',
      channel: 'Online',
      bookerEmail: 'player@example.com',
      customerName: null,
      customerPhone: null,
      status: 'PendingVerification',
      paymentState: 'NotReceived',
      totalBaht: 400,
      refundDueBaht: 0,
      sentBackBaht: 0,
      slots: [
        { courtName: 'คอร์ท 1', startsAt: '2026-09-22T11:00:00Z', endsAt: '2026-09-22T12:00:00Z' },
      ],
      history: [
        {
          from: null,
          to: 'Held',
          changedAt: '2026-09-21T09:00:00Z',
          changedByEmail: 'player@example.com',
          cause: null,
          reason: null,
        },
      ],
      hasSlip: true,
    },
    slipViewings: [],
    ...overrides,
  };
}

describe('AdminComplaintsPage', () => {
  let fixture: ComponentFixture<AdminComplaintsPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [AdminComplaintsPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AdminComplaintsPage);
    fixture.detectChanges();
    httpMock
      .expectOne((request) => request.url === '/api/admin/complaints' && request.method === 'GET')
      .flush([summary()]);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function openDetail(detail: object = complaint()): void {
    clickOn(fixture, 'complaint-k1');
    httpMock.expectOne('/api/admin/complaints/k1').flush(detail);
    fixture.detectChanges();
  }

  it('lists the open complaints first', () => {
    expect(textOf(fixture, 'complaint-k1')).toBe('สนามทดสอบ');
  });

  it('opens a complaint with the booking beside it — and does not fetch the slip', () => {
    openDetail();

    expect(textOf(fixture, 'detail-text')).toBe('โอนแล้วแต่สนามไม่ยืนยัน');
    expect(textOf(fixture, 'detail-who')).toContain('player@example.com');
    // Looking at a slip is recorded, so nothing looks on the admin's behalf.
    httpMock.expectNone('/api/admin/complaints/k1/slip');
    expect(elementOf(fixture, 'see-slip')).not.toBeNull();
  });

  it('fetches the slip only when pressed, then lists the look', () => {
    openDetail();

    clickOn(fixture, 'see-slip');
    httpMock
      .expectOne('/api/admin/complaints/k1/slip')
      .flush(new Blob(['x'], { type: 'image/jpeg' }));
    httpMock.expectOne('/api/admin/complaints/k1').flush(
      complaint({
        slipViewings: [{ viewedByEmail: 'admin@example.com', viewedAt: '2026-09-21T10:05:00Z' }],
      }),
    );
    fixture.detectChanges();

    expect(elementOf(fixture, 'slip-image')).not.toBeNull();
    expect(textOf(fixture, 'slip-viewings')).toContain('admin@example.com');
  });

  it('never shows a slip under a complaint other than the one it was asked for', () => {
    openDetail();
    clickOn(fixture, 'see-slip');
    const slip = httpMock.expectOne('/api/admin/complaints/k1/slip');

    // The admin moves on while the slip is still on its way.
    clickOn(fixture, 'complaint-k1');
    fixture.detectChanges();

    expect(slip.cancelled).toBe(true);
    expect(elementOf(fixture, 'slip-image')).toBeNull();
  });

  it('says why the slip was refused, and shows the complaint as it now is', async () => {
    openDetail();
    clickOn(fixture, 'see-slip');
    httpMock.expectOne('/api/admin/complaints/k1/slip').flush(
      new Blob([JSON.stringify({ code: 'complaint.not_open' })], {
        type: 'application/problem+json',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    // The code inside a Blob is read asynchronously, so the page's next step comes after it.
    const reread = await vi.waitFor(() => httpMock.expectOne('/api/admin/complaints/k1'));
    reread.flush(complaint());
    fixture.detectChanges();

    expect(textOf(fixture, 'slip-error')).toBe(TRANSLATIONS.th['error.complaint.not_open']);
  });

  it('drops a list asked for earlier once another is asked for', () => {
    clickOn(fixture, 'filter-Resolved');
    const older = httpMock.expectOne(
      (one) => one.url === '/api/admin/complaints' && one.params.get('status') === 'Resolved',
    );
    clickOn(fixture, 'filter-All');
    httpMock
      .expectOne((one) => one.url === '/api/admin/complaints' && !one.params.has('status'))
      .flush([summary(), summary({ id: 'k2', status: 'Resolved' })]);
    fixture.detectChanges();

    expect(older.cancelled).toBe(true);
    expect(elementOf(fixture, 'complaint-k2')).not.toBeNull();
  });

  it('offers no slip on a resolved complaint', () => {
    openDetail(
      complaint({
        status: 'Resolved',
        resolvedAt: '2026-09-21T11:00:00Z',
        resolvedByEmail: 'admin@example.com',
        resolution: 'ปิดเรื่องแล้ว',
      }),
    );

    expect(elementOf(fixture, 'see-slip')).toBeNull();
    expect(textOf(fixture, 'detail-resolution')).toContain('ปิดเรื่องแล้ว');
  });

  it('will not resolve without saying what was done', () => {
    openDetail();

    submitForm(fixture, 'form:has([data-testid=resolution])');

    httpMock.expectNone('/api/admin/complaints/k1/resolve');
    expect(elementOf(fixture, 'resolution-error')).not.toBeNull();
  });

  it('resolves with what was written', () => {
    openDetail();

    setInput(fixture, '[data-testid=resolution]', 'สนามยืนยันแล้ว');
    submitForm(fixture, 'form:has([data-testid=resolution])');
    const request = httpMock.expectOne('/api/admin/complaints/k1/resolve');
    expect(request.request.body).toEqual({ resolution: 'สนามยืนยันแล้ว' });
    request.flush(
      complaint({
        status: 'Resolved',
        resolution: 'สนามยืนยันแล้ว',
        resolvedAt: '2026-09-21T11:00:00Z',
      }),
    );
    httpMock
      .expectOne((one) => one.url === '/api/admin/complaints' && one.method === 'GET')
      .flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'detail-resolution')).toContain('สนามยืนยันแล้ว');
  });

  it('asks how a complaint came in before opening it', () => {
    setInput(fixture, '[data-testid=complaint-booking]', 'b1');
    setInput(fixture, '[data-testid=complaint-details]', 'ร้องเรียน');
    submitForm(fixture, 'form:has([data-testid=complaint-booking])');

    httpMock.expectNone((one) => one.method === 'POST');
    expect(elementOf(fixture, 'channel-error')).not.toBeNull();

    check(fixture, '[data-testid=channel-Phone]');
    submitForm(fixture, 'form:has([data-testid=complaint-booking])');
    const request = httpMock.expectOne((one) => one.method === 'POST');
    expect(request.request.body).toEqual({
      bookingId: 'b1',
      details: 'ร้องเรียน',
      channel: 'Phone',
    });
    request.flush(complaint());
    httpMock
      .expectOne((one) => one.url === '/api/admin/complaints' && one.method === 'GET')
      .flush([summary()]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'complaint-detail')).not.toBeNull();
  });

  it('says why a complaint could not be opened', () => {
    setInput(fixture, '[data-testid=complaint-booking]', 'nope');
    setInput(fixture, '[data-testid=complaint-details]', 'ร้องเรียน');
    check(fixture, '[data-testid=channel-Email]');
    submitForm(fixture, 'form:has([data-testid=complaint-booking])');
    httpMock
      .expectOne((one) => one.method === 'POST')
      .flush({ code: 'complaint.booking_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(textOf(fixture, 'open-error')).toBe(
      TRANSLATIONS.th['error.complaint.booking_not_found'],
    );
  });
});
