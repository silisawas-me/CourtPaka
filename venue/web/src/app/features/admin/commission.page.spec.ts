import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { AdminCommissionPage } from './commission.page';

function invoice(overrides: Record<string, unknown> = {}) {
  return {
    id: 'i1',
    venueId: 'v1',
    venueName: 'Smash Court',
    number: 'PLT-INV-2027-000001',
    month: '2027-01-01',
    amountBaht: 4000,
    status: 'PaymentSubmitted',
    overdue: false,
    issuedAt: '2027-02-02T02:00:00Z',
    dueOn: '2027-02-16',
    submittedAt: '2027-02-10T04:00:00Z',
    hasEvidence: true,
    paidAt: null,
    refusedReason: null,
    lines: null,
    ...overrides,
  };
}

describe('AdminCommissionPage', () => {
  let fixture: ComponentFixture<AdminCommissionPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [AdminCommissionPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(invoices: unknown[] = [invoice()]): void {
    fixture = TestBed.createComponent(AdminCommissionPage);
    fixture.detectChanges();

    // What is waiting to be checked is what the screen opens on: it is the only thing here
    // somebody else is waiting on.
    const asked = httpMock.expectOne((request) => request.url === '/api/admin/commission/invoices');
    expect(asked.request.params.get('status')).toBe('PaymentSubmitted');
    asked.flush(invoices);
    fixture.detectChanges();
  }

  it('says so when there is nothing waiting', () => {
    render([]);

    expect(textOf(fixture, 'nothing-here')).toBe(TRANSLATIONS.th['commission.none']);
  });

  it('names the venue, the amount and where the invoice stands', () => {
    render();

    const row = textOf(fixture, 'invoice-i1');
    expect(row).toContain('Smash Court');
    expect(row).toContain('4,000');
    expect(row).toContain(TRANSLATIONS.th['commission.status.PaymentSubmitted']);
  });

  /** Late is not a status: an invoice that is late is still waiting to be paid (PRD US-21). */
  it('shows late beside the status rather than instead of it', () => {
    render([invoice({ overdue: true })]);

    expect(textOf(fixture, 'overdue-i1')).toBe(TRANSLATIONS.th['commission.overdue']);
    expect(textOf(fixture, 'invoice-i1')).toContain(
      TRANSLATIONS.th['commission.status.PaymentSubmitted'],
    );
  });

  it('takes the invoice out of the list once it is paid', () => {
    render();

    clickOn(fixture, 'paid-i1');
    httpMock
      .expectOne('/api/admin/commission/invoices/i1/paid')
      .flush(invoice({ status: 'Paid', paidAt: '2027-02-11T03:00:00Z' }));
    fixture.detectChanges();

    // The list is the one waiting to be checked, and this one no longer is.
    expect(elementOf(fixture, 'invoice-i1')).toBeNull();
  });

  it('will not send a claim of payment back without saying why', () => {
    render();

    clickOn(fixture, 'refuse-i1');
    fixture.detectChanges();
    clickOn(fixture, 'refuse-confirm');
    fixture.detectChanges();

    expect(textOf(fixture, 'decide-error')).toBe(TRANSLATIONS.th['error.venue.reason_required']);
  });

  it('sends it back with the reason the venue will be shown', () => {
    render();

    clickOn(fixture, 'refuse-i1');
    fixture.detectChanges();
    setInput(fixture, '[data-testid="refuse-reason"]', 'ยอดไม่ตรงกับที่เข้าบัญชี');
    clickOn(fixture, 'refuse-confirm');

    const sent = httpMock.expectOne('/api/admin/commission/invoices/i1/refuse');
    expect(sent.request.body).toEqual({ reason: 'ยอดไม่ตรงกับที่เข้าบัญชี' });
    sent.flush(invoice({ status: 'Issued', refusedReason: 'ยอดไม่ตรงกับที่เข้าบัญชี' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'invoice-i1')).toBeNull();
  });

  it('offers the platform a look at what the venue sent', () => {
    render();

    expect(elementOf<HTMLAnchorElement>(fixture, 'evidence-i1')?.getAttribute('href')).toBe(
      '/api/admin/commission/invoices/i1/evidence',
    );
  });

  it('asks for another list when a different filter is chosen', () => {
    render();

    clickOn(fixture, 'filter-All');

    const asked = httpMock.expectOne((request) => request.url === '/api/admin/commission/invoices');
    expect(asked.request.params.has('status')).toBe(false);
    asked.flush([]);
  });
});
