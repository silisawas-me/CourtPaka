import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenuesPage } from './venues.page';

describe('VenuesPage', () => {
  let fixture: ComponentFixture<VenuesPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenuesPage],
      providers: pageProviders([{ path: 'venues/apply', children: [] }]),
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

  it('sends people to the application rather than carrying the form itself', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    // Applying grew a tax identity and a bank account, and moved to its own page.
    expect(elementOf<HTMLAnchorElement>(fixture, 'apply-link')?.getAttribute('href')).toBe(
      '/venues/apply',
    );
  });
});
