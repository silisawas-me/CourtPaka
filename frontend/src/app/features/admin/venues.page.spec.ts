import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { AdminVenuesPage } from './venues.page';

function venue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'v1',
    code: 'SBC',
    name: 'Smash Court',
    addressLine: '1 ถนนทดสอบ',
    district: 'บางรัก',
    province: 'กรุงเทพมหานคร',
    status: 'Pending',
    createdAt: '2026-09-20T10:00:00Z',
    ...overrides,
  };
}

function detail(overrides: Record<string, unknown> = {}) {
  return {
    venue: venue(overrides),
    business: {
      promptPayId: '0812345678',
      promptPayAccountName: 'บริษัท ทดสอบ จำกัด',
      isVatRegistered: true,
      legalName: 'บริษัท ทดสอบ จำกัด',
      taxId: '0105561000000',
      taxBranch: '00000',
      billingAddress: '1 ถนนทดสอบ',
      latitude: null,
      longitude: null,
    },
    agreementVersion: '2026-09-01',
    agreementAcceptedAt: '2026-09-20T10:00:00Z',
    history: [{ from: null, to: 'Pending', changedAt: '2026-09-20T10:00:00Z', reason: null }],
  };
}

describe('AdminVenuesPage', () => {
  let fixture: ComponentFixture<AdminVenuesPage>;
  let httpMock: HttpTestingController;

  function render(venues: unknown[] = [venue()]): void {
    TestBed.configureTestingModule({
      imports: [AdminVenuesPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AdminVenuesPage);
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/admin/venues').flush(venues);
    fixture.detectChanges();
  }

  function openFirst(answer: object = detail(), charged: object = noRateYet): void {
    clickOn(fixture, 'open-v1');
    httpMock.expectOne('/api/admin/venues/v1').flush(answer);
    // The rate the platform charges is read with the application (PRD US-21).
    httpMock.expectOne('/api/admin/venues/v1/commission').flush(charged);
    fixture.detectChanges();
  }

  /** A venue the platform has never agreed a rate with, which is most of them. */
  const noRateYet = { todayPercent: null, rates: [] };

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('opens on what is waiting', () => {
    render();

    const asked = httpMock.expectNone('/api/admin/venues?status=Approved');
    expect(textOf(fixture, 'venue-v1')).toContain('Smash Court');
    expect(asked).toBeUndefined();
  });

  /**
   * Judging an application means reading it, so what the venue said is on screen beside the
   * decision rather than a navigation away (PRD US-20).
   */
  it('shows the tax identity and the account beside the decision', () => {
    render();
    openFirst();

    expect(textOf(fixture, 'tax-id')).toContain('0105561000000');
    expect(textOf(fixture, 'promptpay')).toContain('0812345678');
    expect(textOf(fixture, 'agreement')).toContain('2026-09-01');
    expect(elementOf(fixture, 'approve')).not.toBeNull();
  });

  it('approves without asking for a reason', () => {
    render();
    openFirst();

    clickOn(fixture, 'approve');

    const decided = httpMock.expectOne('/api/admin/venues/v1/approve');
    expect(decided.request.body.reason).toBeNull();
    decided.flush(venue({ status: 'Approved' }));

    // The list is filtered by standing, so it is read again.
    httpMock.expectOne((request) => request.url === '/api/admin/venues').flush([]);
    httpMock.expectOne('/api/admin/venues/v1').flush(detail({ status: 'Approved' }));
    fixture.detectChanges();
  });

  it('will not turn a venue away without saying why', () => {
    render();
    openFirst();

    clickOn(fixture, 'reject');
    clickOn(fixture, 'confirm');

    httpMock.expectNone('/api/admin/venues/v1/reject');
    expect(elementOf(fixture, 'reason-error')).not.toBeNull();
  });

  it('sends the reason the venue will be shown, word for word', () => {
    render();
    openFirst();

    clickOn(fixture, 'reject');
    setInput(fixture, '[data-testid="reason"]', 'เลขประจำตัวผู้เสียภาษีไม่ตรง');
    clickOn(fixture, 'confirm');

    const decided = httpMock.expectOne('/api/admin/venues/v1/reject');
    expect(decided.request.body.reason).toBe('เลขประจำตัวผู้เสียภาษีไม่ตรง');
    decided.flush(venue({ status: 'Rejected' }));

    httpMock.expectOne((request) => request.url === '/api/admin/venues').flush([]);
    httpMock.expectOne('/api/admin/venues/v1').flush(detail({ status: 'Rejected' }));
    fixture.detectChanges();
  });

  it('offers the suspension to a venue that is trading, and lifting it to one that is not', () => {
    render([venue({ status: 'Approved' })]);
    openFirst(detail({ status: 'Approved' }));

    expect(elementOf(fixture, 'suspend')).not.toBeNull();
    expect(elementOf(fixture, 'approve')).toBeNull();
  });

  it('says so when a filter holds nothing', () => {
    render([]);

    expect(textOf(fixture, 'nothing-here')).toBe(TRANSLATIONS.th['admin.venues.none']);
  });

  /**
   * What the platform charges this venue (PRD US-21). Read with the application, because the
   * last rate is what an admin needs in front of them to decide the next one.
   */
  it('says a venue has no rate yet rather than saying it is charged nothing', () => {
    render();
    openFirst();

    expect(textOf(fixture, 'commission-today')).toContain(TRANSLATIONS.th['admin.commission.none']);
    expect(elementOf(fixture, 'commission-history')).toBeNull();
  });

  it('shows what is charged today and every rate before it', () => {
    render();
    openFirst(detail(), {
      todayPercent: 10,
      rates: [
        {
          percent: 8,
          effectiveFrom: '2027-06-01',
          setAt: '2027-05-01T02:00:00Z',
          setByEmail: 'a@b.c',
          note: null,
        },
        {
          percent: 10,
          effectiveFrom: '2027-01-01',
          setAt: '2026-12-01T02:00:00Z',
          setByEmail: 'a@b.c',
          note: 'ตามที่ตกลง',
        },
      ],
    });

    // Ten today, because the eight has not started yet — the platform tells a venue first.
    expect(textOf(fixture, 'commission-today')).toContain('10%');
    expect(textOf(fixture, 'commission-history')).toContain('8%');
    expect(textOf(fixture, 'commission-history')).toContain('ตามที่ตกลง');
  });

  it('agrees a rate from a date and draws what comes back', () => {
    render();
    openFirst();

    setInput(fixture, '[data-testid="rate-percent"]', '12.5');
    clickOn(fixture, 'save-rate');

    const sent = httpMock.expectOne('/api/admin/venues/v1/commission');
    expect(sent.request.body.percent).toBe(12.5);
    expect(sent.request.body.effectiveFrom).toMatch(/^\d{4}-\d{2}-\d{2}$/);

    sent.flush({
      todayPercent: 12.5,
      rates: [
        {
          percent: 12.5,
          effectiveFrom: sent.request.body.effectiveFrom,
          setAt: '2027-01-01T02:00:00Z',
          setByEmail: 'a@b.c',
          note: null,
        },
      ],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'commission-today')).toContain('12.5%');
  });

  it('translates a rate the server will not take', () => {
    render();
    openFirst();

    setInput(fixture, '[data-testid="rate-percent"]', '101');
    clickOn(fixture, 'save-rate');

    httpMock
      .expectOne('/api/admin/venues/v1/commission')
      .flush({ code: 'venue.invalid_rate' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'rate-error')).toBe(TRANSLATIONS.th['error.venue.invalid_rate']);
  });
});
