import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { check, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { VenueApplyPage } from './apply.page';

describe('VenueApplyPage', () => {
  let fixture: ComponentFixture<VenueApplyPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenueApplyPage],
      // The page navigates to the new venue once the application is in.
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenueApplyPage);
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/agreement').flush({ version: '2026-09-01' });
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  /** Everything the platform asks for before a venue can take money (PRD US-10). */
  function fillForm(): void {
    setInput(fixture, '#code', 'SBC');
    setInput(fixture, '#name', 'Smash Court');
    setInput(fixture, '#address-line', '1 ถนนทดสอบ');
    setInput(fixture, '#district', 'บางรัก');
    setInput(fixture, '#province', 'กรุงเทพมหานคร');
    setInput(fixture, '#promptpay-id', '0812345678');
    setInput(fixture, '#promptpay-name', 'บริษัท ทดสอบ จำกัด');
    setInput(fixture, '#legal-name', 'บริษัท ทดสอบ จำกัด');
    setInput(fixture, '#tax-id', '0105561000000');
    setInput(fixture, '#billing-address', '1 ถนนทดสอบ บางรัก กรุงเทพมหานคร');
    check(fixture, '[data-testid="accepts-agreement"]');
  }

  it('sends the whole application, with the agreement version it was shown', () => {
    fillForm();
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues');
    expect(request.request.body.code).toBe('SBC');
    expect(request.request.body.district).toBe('บางรัก');
    expect(request.request.body.business.taxId).toBe('0105561000000');
    expect(request.request.body.business.taxBranch).toBe('00000');
    expect(request.request.body.business.isVatRegistered).toBe(false);

    // What was on screen is what is recorded as accepted (PRD US-10, Q8).
    expect(request.request.body.agreementVersion).toBe('2026-09-01');

    request.flush({ id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Pending' });
  });

  it('will not apply until the agreement is accepted', () => {
    setInput(fixture, '#code', 'SBC');
    setInput(fixture, '#name', 'Smash Court');
    setInput(fixture, '#address-line', '1 ถนนทดสอบ');
    setInput(fixture, '#district', 'บางรัก');
    setInput(fixture, '#province', 'กรุงเทพมหานคร');
    setInput(fixture, '#promptpay-id', '0812345678');
    setInput(fixture, '#promptpay-name', 'บริษัท ทดสอบ จำกัด');
    setInput(fixture, '#legal-name', 'บริษัท ทดสอบ จำกัด');
    setInput(fixture, '#tax-id', '0105561000000');
    setInput(fixture, '#billing-address', '1 ถนนทดสอบ');

    submitForm(fixture);

    httpMock.expectNone('/api/venues');
  });

  it('does not call the API for a tax number that is not thirteen digits', () => {
    fillForm();
    setInput(fixture, '#tax-id', '12345');
    submitForm(fixture);

    httpMock.expectNone('/api/venues');
    expect(textOf(fixture, 'tax-id-error')).toBeTruthy();
  });

  it('offers the court address as the address for documents', () => {
    setInput(fixture, '#address-line', '1 ถนนทดสอบ');
    setInput(fixture, '#district', 'บางรัก');
    setInput(fixture, '#province', 'กรุงเทพมหานคร');

    fixture.nativeElement.querySelector('[data-testid="copy-address"]').click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('#billing-address').value).toBe(
      '1 ถนนทดสอบ บางรัก กรุงเทพมหานคร',
    );
  });

  it('translates a rejected code', () => {
    fillForm();
    submitForm(fixture);

    httpMock
      .expectOne('/api/venues')
      .flush({ code: 'venue.code_already_used' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['error.venue.code_already_used']);
  });
});
