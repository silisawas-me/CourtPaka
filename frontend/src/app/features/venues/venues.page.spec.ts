import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { VenuesPage } from './venues.page';

describe('VenuesPage', () => {
  let fixture: ComponentFixture<VenuesPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenuesPage],
      // The page navigates to the new venue once it is created.
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenuesPage);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('says so when the account runs no venues yet', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'no-venues')).toBe(TRANSLATIONS.th['venues.none']);
  });

  it('lists the venues the account belongs to', () => {
    httpMock
      .expectOne('/api/venues/mine')
      .flush([{ id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Approved' }]);
    fixture.detectChanges();

    expect(textOf(fixture, 'venue-list')).toContain('Smash Court');
  });

  it('creates a venue with the code and name entered', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    setInput(fixture, '#code', 'SBC');
    setInput(fixture, '#name', 'Smash Court');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues');
    expect(request.request.body).toEqual({ code: 'SBC', name: 'Smash Court' });
    request.flush({ id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Pending' });
  });

  it('translates a rejected venue code', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    setInput(fixture, '#code', 'SBC');
    setInput(fixture, '#name', 'Smash Court');
    submitForm(fixture);

    httpMock
      .expectOne('/api/venues')
      .flush({ code: 'venue.code_already_used' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['error.venue.code_already_used']);
  });

  it('does not call the API for a code that is too short', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    setInput(fixture, '#code', 'AB');
    setInput(fixture, '#name', 'Smash Court');
    submitForm(fixture);

    httpMock.expectNone('/api/venues');
  });
});
